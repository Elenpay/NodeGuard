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
using Microsoft.Extensions.Logging.Abstractions;
using NodeGuard.Data;
using NodeGuard.Data.Models;
using Nodeguard;
using Wallet = NodeGuard.Data.Models.Wallet;
using NSubstitute;

namespace NodeGuard.Services;

public class WithdrawalEventPublisherTests
{
    private const int WalletId = 5;
    private const long Now = 1_700_000_000_000;
    private const string TxId = "4f7c3a1b2d5e6f708192a3b4c5d6e7f8091a2b3c4d5e6f708192a3b4c5d6e7f8";

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(Now);
    }

    private readonly IWalletEventHub _hub = Substitute.For<IWalletEventHub>();
    private readonly IDbContextFactory<ApplicationDbContext> _factory = Substitute.For<IDbContextFactory<ApplicationDbContext>>();
    private readonly WithdrawalEventPublisher _publisher;
    private readonly DbContextOptions<ApplicationDbContext> _options = new DbContextOptionsBuilder<ApplicationDbContext>()
        .UseInMemoryDatabase("WithdrawalEventPublisherTests" + Guid.NewGuid())
        .Options;

    public WithdrawalEventPublisherTests()
    {
        using (var context = new ApplicationDbContext(_options))
        {
            context.Wallets.Add(new Wallet { Id = WalletId, Name = "cold", MofN = 2, IsHotWallet = false });
            context.SaveChanges();
        }

        _factory.CreateDbContext().Returns(_ => new ApplicationDbContext(_options));
        _hub.HasSubscribers(WalletId).Returns(true);
        _publisher = new WithdrawalEventPublisher(_hub, _factory, new FixedTime(), NullLogger<WithdrawalEventPublisher>.Instance);
    }

    private int Seed(WalletWithdrawalRequest request)
    {
        using var context = new ApplicationDbContext(_options);
        request.WalletId = WalletId;
        request.Description ??= "publisher test";
        context.WalletWithdrawalRequests.Add(request);
        context.SaveChanges();
        return request.Id;
    }

    private WalletEvent SinglePublishedEvent()
    {
        var calls = _hub.ReceivedCalls().Where(c => c.GetMethodInfo().Name == nameof(IWalletEventHub.Publish)).ToList();
        calls.Should().ContainSingle();
        return (WalletEvent)calls[0].GetArguments()[0]!;
    }

    [Fact]
    public void PublishTransition_UnwatchedWallet_DoesNothing()
    {
        _hub.HasSubscribers(WalletId).Returns(false);

        _publisher.PublishTransition(WalletId, 1, WalletWithdrawalRequestStatus.FinalizingPSBT, null,
            WalletWithdrawalRequestStatus.OnChainConfirmationPending, TxId);

        _factory.DidNotReceive().CreateDbContext();
        _hub.DidNotReceive().Publish(Arg.Any<WalletEvent>());
    }

    [Fact]
    public void PublishTransition_NothingChanged_PublishesNothing()
    {
        _publisher.PublishTransition(WalletId, 1, WalletWithdrawalRequestStatus.Pending, null,
            WalletWithdrawalRequestStatus.Pending, null);

        _hub.DidNotReceive().Publish(Arg.Any<WalletEvent>());
    }

    [Fact]
    public void PublishTransition_Broadcast_PublishesTheStoredSnapshot()
    {
        var id = Seed(new WalletWithdrawalRequest
        {
            Status = WalletWithdrawalRequestStatus.OnChainConfirmationPending,
            TxId = TxId,
            ReferenceId = "ref",
            WalletWithdrawalRequestDestinations = [new() { Address = "bcrt1qdest", Amount = 0.01m }]
        });

        _publisher.PublishTransition(WalletId, id, WalletWithdrawalRequestStatus.FinalizingPSBT, null,
            WalletWithdrawalRequestStatus.OnChainConfirmationPending, TxId);

        var e = SinglePublishedEvent();
        e.Type.Should().Be(WALLET_EVENT_TYPE.WalletEventWithdrawalBroadcast);
        e.WalletId.Should().Be(WalletId);
        e.OccurredAtUnixMs.Should().Be(Now);
        e.Withdrawal.RequestId.Should().Be(id);
        e.Withdrawal.TxId.Should().Be(TxId);
        e.Withdrawal.ReferenceId.Should().Be("ref");
        e.Withdrawal.Status.Should().Be(WITHDRAWAL_REQUEST_STATUS.WithdrawalPendingConfirmation);
        e.Withdrawal.PreviousStatus.Should().Be(WITHDRAWAL_REQUEST_STATUS.WithdrawalPendingApproval);
        e.Withdrawal.AmountSats.Should().Be(1_000_000);
        e.Withdrawal.HasReplacedByRequestId.Should().BeFalse();
    }

    [Fact]
    public void PublishTransition_Bumped_NamesTheLatestReplacement()
    {
        var originalId = Seed(new WalletWithdrawalRequest { Status = WalletWithdrawalRequestStatus.Bumped, TxId = TxId });
        Seed(new WalletWithdrawalRequest
        {
            Status = WalletWithdrawalRequestStatus.Cancelled, BumpingWalletWithdrawalRequestId = originalId
        });
        var latestReplacementId = Seed(new WalletWithdrawalRequest
        {
            Status = WalletWithdrawalRequestStatus.PSBTSignaturesPending, BumpingWalletWithdrawalRequestId = originalId
        });

        _publisher.PublishTransition(WalletId, originalId, WalletWithdrawalRequestStatus.OnChainConfirmationPending, TxId,
            WalletWithdrawalRequestStatus.Bumped, TxId);

        var e = SinglePublishedEvent();
        e.Type.Should().Be(WALLET_EVENT_TYPE.WalletEventWithdrawalBumped);
        e.Withdrawal.ReplacedByRequestId.Should().Be(latestReplacementId);
    }

    [Fact]
    public void PublishSignatureAdded_CountsTheHumanSignatures()
    {
        var id = Seed(new WalletWithdrawalRequest
        {
            Status = WalletWithdrawalRequestStatus.PSBTSignaturesPending,
            WalletWithdrawalRequestPSBTs =
            [
                new() { IsTemplatePSBT = true, PSBT = "template" },
                new() { PSBT = "signed-by-a" }
            ]
        });

        _publisher.PublishSignatureAdded(WalletId, id);

        var e = SinglePublishedEvent();
        e.Type.Should().Be(WALLET_EVENT_TYPE.WalletEventWithdrawalSignatureAdded);
        e.Withdrawal.SignaturesCollected.Should().Be(1);
        e.Withdrawal.SignaturesRequired.Should().Be(2);
        e.Withdrawal.HasPreviousStatus.Should().BeFalse();
    }

    [Fact]
    public void PublishCreated_UsesTheRequestInHand()
    {
        var request = new WalletWithdrawalRequest
        {
            Id = 99,
            WalletId = WalletId,
            Status = WalletWithdrawalRequestStatus.Pending,
            Wallet = new Wallet { Id = WalletId, MofN = 2 },
            WalletWithdrawalRequestDestinations = [new() { Address = "bcrt1qdest", Amount = 0.02m }]
        };

        _publisher.PublishCreated(request);

        _factory.DidNotReceive().CreateDbContext();
        var e = SinglePublishedEvent();
        e.Type.Should().Be(WALLET_EVENT_TYPE.WalletEventWithdrawalCreated);
        e.Withdrawal.RequestId.Should().Be(99);
        e.Withdrawal.AmountSats.Should().Be(2_000_000);
    }

    [Fact]
    public void Publish_DatabaseFailure_IsSwallowed()
    {
        _factory.CreateDbContext().Returns(_ => throw new InvalidOperationException("database down"));

        var act = () => _publisher.PublishTransition(WalletId, 1, WalletWithdrawalRequestStatus.FinalizingPSBT, null,
            WalletWithdrawalRequestStatus.OnChainConfirmationPending, TxId);

        act.Should().NotThrow();
        _hub.DidNotReceive().Publish(Arg.Any<WalletEvent>());
    }

    [Fact]
    public void ReadBeforeUpdate_UnwatchedWallet_DoesNotReadTheDatabase()
    {
        _hub.HasSubscribers(WalletId).Returns(false);
        var id = Seed(new WalletWithdrawalRequest { Status = WalletWithdrawalRequestStatus.FinalizingPSBT });
        _factory.ClearReceivedCalls();

        var before = _publisher.ReadBeforeUpdate(new WalletWithdrawalRequest { Id = id, WalletId = WalletId });

        before.Should().BeNull();
        _factory.DidNotReceive().CreateDbContext();
    }

    [Fact]
    public void ReadBeforeUpdate_WatchedWallet_ReturnsTheStoredState()
    {
        var id = Seed(new WalletWithdrawalRequest { Status = WalletWithdrawalRequestStatus.OnChainConfirmationPending, TxId = TxId });

        // The request in hand already carries the new values; the stored ones are what the update changes
        var before = _publisher.ReadBeforeUpdate(new WalletWithdrawalRequest
        {
            Id = id,
            WalletId = WalletId,
            Status = WalletWithdrawalRequestStatus.OnChainConfirmed,
            TxId = TxId
        });

        before.Should().Be(new WithdrawalStateBeforeUpdate(WalletId, id,
            WalletWithdrawalRequestStatus.OnChainConfirmationPending, TxId));
    }

    [Fact]
    public void ReadBeforeUpdate_UnsavedRequest_ReturnsNull()
    {
        _publisher.ReadBeforeUpdate(new WalletWithdrawalRequest { WalletId = WalletId }).Should().BeNull();

        _factory.DidNotReceive().CreateDbContext();
    }

    [Fact]
    public void ReadBeforeUpdate_DatabaseFailure_ReturnsNull()
    {
        _factory.CreateDbContext().Returns(_ => throw new InvalidOperationException("database down"));

        var act = () => _publisher.ReadBeforeUpdate(new WalletWithdrawalRequest { Id = 1, WalletId = WalletId });

        act.Should().NotThrow().Which.Should().BeNull();
    }

    [Fact]
    public void PublishUpdated_PublishesTheTransitionFromTheStateBefore()
    {
        var id = Seed(new WalletWithdrawalRequest { Status = WalletWithdrawalRequestStatus.OnChainConfirmationPending, TxId = TxId });

        _publisher.PublishUpdated(
            new WithdrawalStateBeforeUpdate(WalletId, id, WalletWithdrawalRequestStatus.FinalizingPSBT, null),
            new WalletWithdrawalRequest
            {
                Id = id,
                WalletId = WalletId,
                Status = WalletWithdrawalRequestStatus.OnChainConfirmationPending,
                TxId = TxId
            });

        var e = SinglePublishedEvent();
        e.Type.Should().Be(WALLET_EVENT_TYPE.WalletEventWithdrawalBroadcast);
        e.Withdrawal.PreviousStatus.Should().Be(WITHDRAWAL_REQUEST_STATUS.WithdrawalPendingApproval);
    }

    [Fact]
    public void PublishUpdated_NothingReadBefore_PublishesNothing()
    {
        _publisher.PublishUpdated(null, new WalletWithdrawalRequest
        {
            Id = 1,
            WalletId = WalletId,
            Status = WalletWithdrawalRequestStatus.OnChainConfirmed,
            TxId = TxId
        });

        _factory.DidNotReceive().CreateDbContext();
        _hub.DidNotReceive().Publish(Arg.Any<WalletEvent>());
    }
}
