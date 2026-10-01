/*
 * NodeGuard
 * Copyright (C) 2023  Elenpay
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program.  If not, see http://www.gnu.org/licenses/.
 *
 */

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NodeGuard.Data.Models;

namespace NodeGuard.Data.Repositories;

/// <summary>
/// The upsert owns when a peer may be proposed again, so these cover the recycling rules rather
/// than the persistence mechanics.
/// </summary>
public class ChannelOpenRecommendationRepositoryTests
{
    private readonly Random _random = new();
    private const int NodeId = 1;
    private const string Peer = "03aaa";

    private (ChannelOpenRecommendationRepository sut, ApplicationDbContext seed) SetupDb()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: "ChannelOpenRec" + _random.Next())
            .Options;

        var factory = new Mock<IDbContextFactory<ApplicationDbContext>>();
        factory.Setup(x => x.CreateDbContext()).Returns(() => new ApplicationDbContext(options));
        factory.Setup(x => x.CreateDbContextAsync(default)).ReturnsAsync(() => new ApplicationDbContext(options));

        var logger = new Mock<ILogger<ChannelOpenRecommendationRepository>>();

        return (new ChannelOpenRecommendationRepository(factory.Object, logger.Object), new ApplicationDbContext(options));
    }

    private static ChannelOpenRecommendation Stored(
        ChannelOpenRecommendationStatus status,
        int? requestId = null,
        string? dismissReason = null)
        => new()
        {
            NodeId = NodeId,
            PeerPubKey = Peer,
            SuggestedCapacitySats = 1_000_000,
            Status = status,
            ChannelOperationRequestId = requestId,
            DismissReason = dismissReason,
            LastEvidenceAt = DateTimeOffset.UtcNow.AddHours(-2)
        };

    /// <summary>A freshly planned row for the same peer, distinguishable by its capacity.</summary>
    private static ChannelOpenRecommendation Plan()
        => new()
        {
            NodeId = NodeId,
            PeerPubKey = Peer,
            SuggestedCapacitySats = 4_000_000,
            Bursts = 7,
            Status = ChannelOpenRecommendationStatus.Open,
            LastEvidenceAt = DateTimeOffset.UtcNow
        };

    [Fact]
    public async Task Upsert_ExpiredRow_IsReopenedWithTheNewEvidence()
    {
        var (sut, seed) = SetupDb();
        seed.ChannelOpenRecommendations.Add(Stored(ChannelOpenRecommendationStatus.Expired));
        await seed.SaveChangesAsync();

        var (persisted, error) = await sut.Upsert(Plan());

        error.Should().BeNull();
        persisted!.Status.Should().Be(ChannelOpenRecommendationStatus.Open);
        persisted.SuggestedCapacitySats.Should().Be(4_000_000);
        persisted.Bursts.Should().Be(7);
    }

    [Fact]
    public async Task Upsert_DismissedRow_IsRecycledAndTheReasonCleared()
    {
        var (sut, seed) = SetupDb();
        seed.ChannelOpenRecommendations.Add(
            Stored(ChannelOpenRecommendationStatus.Dismissed, dismissReason: "not now"));
        await seed.SaveChangesAsync();

        // Only a peer already past its cooldown reaches the upsert, so a dismissal is a suppression
        // and not a permanent exclusion.
        var (persisted, error) = await sut.Upsert(Plan());

        error.Should().BeNull();
        persisted!.Status.Should().Be(ChannelOpenRecommendationStatus.Open);
        persisted.DismissReason.Should().BeNull();
        persisted.SuggestedCapacitySats.Should().Be(4_000_000);
    }

    [Fact]
    public async Task Upsert_FailedRow_IsRecycledAndTheLinkCleared()
    {
        var (sut, seed) = SetupDb();
        seed.ChannelOpenRecommendations.Add(
            Stored(ChannelOpenRecommendationStatus.Failed, requestId: 55));
        await seed.SaveChangesAsync();

        var (persisted, error) = await sut.Upsert(Plan());

        // The peer never got the channel and carries no cooldown, so the next run that still sees the
        // demand must be able to propose it again.
        error.Should().BeNull();
        persisted!.Status.Should().Be(ChannelOpenRecommendationStatus.Open);
        persisted.SuggestedCapacitySats.Should().Be(4_000_000);
        persisted.ChannelOperationRequestId.Should().BeNull();
    }

    [Fact]
    public async Task Upsert_PromotedRowWhoseOpenIsStillUnresolved_IsLeftUntouched()
    {
        var (sut, seed) = SetupDb();
        seed.ChannelOperationRequests.Add(new ChannelOperationRequest
        {
            Id = 55,
            RequestType = OperationRequestType.Open,
            Status = ChannelOperationRequestStatus.Pending
        });
        seed.ChannelOpenRecommendations.Add(Stored(ChannelOpenRecommendationStatus.Promoted, requestId: 55));
        await seed.SaveChangesAsync();

        var (persisted, error) = await sut.Upsert(Plan());

        error.Should().BeNull();
        persisted!.Status.Should().Be(ChannelOpenRecommendationStatus.Promoted);
        persisted.SuggestedCapacitySats.Should().Be(1_000_000);
        persisted.ChannelOperationRequestId.Should().Be(55);
    }

    [Theory]
    [InlineData(ChannelOperationRequestStatus.OnChainConfirmed)]
    [InlineData(ChannelOperationRequestStatus.Failed)]
    [InlineData(ChannelOperationRequestStatus.Cancelled)]
    [InlineData(ChannelOperationRequestStatus.Rejected)]
    public async Task Upsert_PromotedRowWhoseOpenResolved_IsRecycledAndTheLinkCleared(
        ChannelOperationRequestStatus resolved)
    {
        var (sut, seed) = SetupDb();
        seed.ChannelOperationRequests.Add(new ChannelOperationRequest
        {
            Id = 55,
            RequestType = OperationRequestType.Open,
            Status = resolved
        });
        seed.ChannelOpenRecommendations.Add(Stored(ChannelOpenRecommendationStatus.Promoted, requestId: 55));
        await seed.SaveChangesAsync();

        var (persisted, error) = await sut.Upsert(Plan());

        error.Should().BeNull();
        persisted!.Status.Should().Be(ChannelOpenRecommendationStatus.Open);
        persisted.SuggestedCapacitySats.Should().Be(4_000_000);
        persisted.ChannelOperationRequestId.Should().BeNull();
    }

    [Fact]
    public async Task Upsert_NoRowForThePeer_Inserts()
    {
        var (sut, _) = SetupDb();

        var (persisted, error) = await sut.Upsert(Plan());

        error.Should().BeNull();
        persisted!.Status.Should().Be(ChannelOpenRecommendationStatus.Open);
        persisted.PeerPubKey.Should().Be(Peer);
    }

    /// <summary>A stored row for an arbitrary node and peer, for the queries that span several of both.</summary>
    private static ChannelOpenRecommendation Row(
        int nodeId, string peer, ChannelOpenRecommendationStatus status,
        long missedFeeMsat = 0, DateTimeOffset? updatedAt = null)
        => new()
        {
            NodeId = nodeId,
            PeerPubKey = peer,
            Status = status,
            MissedFeeMsat = missedFeeMsat,
            SuggestedCapacitySats = 1_000_000,
            UpdateDatetime = updatedAt ?? DateTimeOffset.UtcNow,
            LastEvidenceAt = DateTimeOffset.UtcNow
        };

    [Fact]
    public async Task GetOpenByNode_ReturnsOnlyThatNodesOpenRows_RichestFirst()
    {
        var (sut, seed) = SetupDb();
        seed.ChannelOpenRecommendations.AddRange(
            Row(NodeId, "03low", ChannelOpenRecommendationStatus.Open, missedFeeMsat: 10),
            Row(NodeId, "03high", ChannelOpenRecommendationStatus.Open, missedFeeMsat: 900),
            Row(NodeId, "03mid", ChannelOpenRecommendationStatus.Open, missedFeeMsat: 300),
            Row(NodeId, "03promoted", ChannelOpenRecommendationStatus.Promoted, missedFeeMsat: 5000),
            Row(NodeId, "03dismissed", ChannelOpenRecommendationStatus.Dismissed, missedFeeMsat: 5000),
            Row(NodeId, "03expired", ChannelOpenRecommendationStatus.Expired, missedFeeMsat: 5000),
            Row(2, "03otherNode", ChannelOpenRecommendationStatus.Open, missedFeeMsat: 5000));
        await seed.SaveChangesAsync();

        var result = await sut.GetOpenByNode(NodeId);

        // A limited budget is spent down this list, so the ordering decides which peer gets funded.
        result.Select(x => x.PeerPubKey).Should().Equal("03high", "03mid", "03low");
    }

    [Fact]
    public async Task GetOpenByNode_NoOpenRows_ReturnsEmpty()
    {
        var (sut, seed) = SetupDb();
        seed.ChannelOpenRecommendations.Add(Stored(ChannelOpenRecommendationStatus.Promoted));
        await seed.SaveChangesAsync();

        var result = await sut.GetOpenByNode(NodeId);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetLastDecisionByPeer_KeepsTheLatestDecisionPerPeerOfThatNode()
    {
        var (sut, seed) = SetupDb();
        var now = DateTimeOffset.UtcNow;
        var latest = now.AddDays(-1);

        seed.ChannelOpenRecommendations.AddRange(
            Row(NodeId, "03a", ChannelOpenRecommendationStatus.Dismissed, updatedAt: now.AddDays(-9)),
            Row(NodeId, "03a", ChannelOpenRecommendationStatus.Promoted, updatedAt: latest),
            Row(NodeId, "03b", ChannelOpenRecommendationStatus.Promoted, updatedAt: now.AddDays(-4)),
            Row(2, "03c", ChannelOpenRecommendationStatus.Promoted, updatedAt: now));
        await seed.SaveChangesAsync();

        var result = await sut.GetLastDecisionByPeer(NodeId);

        // The cooldown runs from the newest decision; an older one must not shorten it.
        result.Keys.Should().BeEquivalentTo(new[] { "03a", "03b" });
        result["03a"].Should().Be(latest);
    }

    [Fact]
    public async Task GetLastDecisionByPeer_IgnoresOpenAndExpiredRows()
    {
        var (sut, seed) = SetupDb();
        seed.ChannelOpenRecommendations.AddRange(
            Row(NodeId, "03open", ChannelOpenRecommendationStatus.Open),
            Row(NodeId, "03expired", ChannelOpenRecommendationStatus.Expired));
        await seed.SaveChangesAsync();

        var result = await sut.GetLastDecisionByPeer(NodeId);

        // Neither is an operator decision. An expired row counting here would put its peer in a
        // cooldown nobody asked for, and the demand would stay unproposed for the whole window.
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetLastDecisionByPeer_IgnoresFailedRows()
    {
        var (sut, seed) = SetupDb();
        seed.ChannelOpenRecommendations.Add(Row(NodeId, Peer, ChannelOpenRecommendationStatus.Failed));
        await seed.SaveChangesAsync();

        var result = await sut.GetLastDecisionByPeer(NodeId);

        // The whole point of the status: a promotion that produced no channel must not suppress the
        // peer, or the demand behind it goes unserved for the length of the cooldown.
        result.Should().BeEmpty();
    }

    [Theory]
    [InlineData(ChannelOperationRequestStatus.Failed)]
    [InlineData(ChannelOperationRequestStatus.Cancelled)]
    public async Task FailUnrealizedPromotions_PromotedRowWhoseChannelNeverOpened_IsMarkedFailed(
        ChannelOperationRequestStatus unrealized)
    {
        var (sut, seed) = SetupDb();
        seed.ChannelOperationRequests.Add(new ChannelOperationRequest
        {
            Id = 55,
            RequestType = OperationRequestType.Open,
            Status = unrealized
        });
        seed.ChannelOpenRecommendations.Add(Stored(ChannelOpenRecommendationStatus.Promoted, requestId: 55));
        await seed.SaveChangesAsync();

        var count = await sut.FailUnrealizedPromotions(NodeId);

        count.Should().Be(1);
        var stored = await seed.ChannelOpenRecommendations.AsNoTracking().SingleAsync();
        stored.Status.Should().Be(ChannelOpenRecommendationStatus.Failed);
    }

    [Theory]
    [InlineData(ChannelOperationRequestStatus.Pending)]
    [InlineData(ChannelOperationRequestStatus.OnChainConfirmationPending)]
    [InlineData(ChannelOperationRequestStatus.OnChainConfirmed)]
    [InlineData(ChannelOperationRequestStatus.Rejected)]
    public async Task FailUnrealizedPromotions_PromotedRowWhoseOpenIsLiveOrDecided_StaysPromoted(
        ChannelOperationRequestStatus kept)
    {
        var (sut, seed) = SetupDb();
        seed.ChannelOperationRequests.Add(new ChannelOperationRequest
        {
            Id = 55,
            RequestType = OperationRequestType.Open,
            Status = kept
        });
        seed.ChannelOpenRecommendations.Add(Stored(ChannelOpenRecommendationStatus.Promoted, requestId: 55));
        await seed.SaveChangesAsync();

        var count = await sut.FailUnrealizedPromotions(NodeId);

        // An open still on its way keeps its peer suppressed so the run cannot double the intent, and
        // a rejection is an operator turning the peer down — a decision, like a dismissal.
        count.Should().Be(0);
        var stored = await seed.ChannelOpenRecommendations.AsNoTracking().SingleAsync();
        stored.Status.Should().Be(ChannelOpenRecommendationStatus.Promoted);
    }

    [Fact]
    public async Task FailUnrealizedPromotions_LeavesOtherNodesAndUnpromotedRowsAlone()
    {
        var (sut, seed) = SetupDb();
        seed.ChannelOperationRequests.Add(new ChannelOperationRequest
        {
            Id = 55,
            RequestType = OperationRequestType.Open,
            Status = ChannelOperationRequestStatus.Failed
        });
        var otherNodesRow = Row(2, "03otherNode", ChannelOpenRecommendationStatus.Promoted);
        otherNodesRow.ChannelOperationRequestId = 55;
        seed.ChannelOpenRecommendations.AddRange(
            otherNodesRow,
            Row(NodeId, "03dismissed", ChannelOpenRecommendationStatus.Dismissed),
            Row(NodeId, "03open", ChannelOpenRecommendationStatus.Open));
        await seed.SaveChangesAsync();

        var count = await sut.FailUnrealizedPromotions(NodeId);

        count.Should().Be(0);
        var statuses = await seed.ChannelOpenRecommendations.AsNoTracking()
            .ToDictionaryAsync(x => x.PeerPubKey, x => x.Status);
        statuses["03otherNode"].Should().Be(ChannelOpenRecommendationStatus.Promoted);
        statuses["03dismissed"].Should().Be(ChannelOpenRecommendationStatus.Dismissed);
        statuses["03open"].Should().Be(ChannelOpenRecommendationStatus.Open);
    }
}
