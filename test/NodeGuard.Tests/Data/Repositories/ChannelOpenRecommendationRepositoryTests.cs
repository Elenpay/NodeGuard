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
}
