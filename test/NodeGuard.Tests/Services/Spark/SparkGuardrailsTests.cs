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
using Microsoft.Extensions.Logging.Abstractions;
using NodeGuard.Data.Models;
using NSpark.Models;
using NSubstitute;

namespace NodeGuard.Services.Spark;

public class SparkGuardrailsTests
{
    private static readonly SparkWalletRef A = new(1);
    private static readonly SparkWalletRef B = new(2);

    private readonly ISparkWalletService _spark = Substitute.For<ISparkWalletService>();
    private readonly ManualTime _time = new();
    private SparkGuardrails _guardrails;

    public SparkGuardrailsTests()
    {
        Wallets(new SparkWalletEntry(A, "transit-a", "02aa", 10_000_000));
        _spark.EnsureReadyAsync(Arg.Any<SparkWalletRef>(), Arg.Any<CancellationToken>())
            .Returns(new SparkWalletStatus(SparkWalletState.Ready, "02aa"));
        Holds(A, 0);
        Holds(B, 0);
        _guardrails = Guardrails(ExitSpeed.Slow);
    }

    private SparkGuardrails Guardrails(ExitSpeed exitSpeed) =>
        new(new SparkSettings { Enabled = true, ExitSpeed = exitSpeed }, _spark, NullLogger<SparkGuardrails>.Instance, _time);

    private void Wallets(params SparkWalletEntry[] wallets) =>
        _spark.GetWalletsAsync(Arg.Any<CancellationToken>()).Returns(wallets);

    private void Holds(SparkWalletRef wallet, long owned, long incoming = 0) =>
        _spark.GetBalanceAsync(wallet, Arg.Any<CancellationToken>())
            .Returns(new WalletBalance(new SatsBalance(owned, owned, incoming), [], []));

    private static SwapOut PaidSparkSwap(DateTimeOffset created, string identity = "02aa", long? received = null,
        string? txId = null) => new()
    {
        Id = 5, Provider = SwapProvider.Spark, Status = SwapOutStatus.Pending, ProviderId = "req-5", LightningFeeSats = 3,
        SatsAmount = 500_000, SparkReceivedSats = received, TxId = txId, SparkIdentity = identity, CreationDatetime = created
    };

    [Fact]
    public async Task TheBalance_IsCachedWhenChecked()
    {
        Holds(A, 1_000, incoming: 500);

        await _guardrails.CheckAsync([]);

        _guardrails.LastBalance(A).Should().BeEquivalentTo(new SparkBalanceSnapshot(1_000, 1_000, 500, _time.GetUtcNow()));
        _guardrails.LastBalance(B).Should().BeNull();
    }

    [Fact]
    public async Task SatsStuckWithNoSwapInFlight_AreAlertedOnceAfterAnHour()
    {
        Holds(A, 42_000);

        (await _guardrails.CheckAsync([])).Should().BeEmpty();
        _guardrails.StuckSince(A).Should().Be(_time.GetUtcNow());

        _time.Advance(SparkGuardrails.StuckAlertAfter);
        var alerts = await _guardrails.CheckAsync([]);
        alerts.Should().ContainSingle().Which.Should().Match<SparkAlert>(a =>
            a.Action == AuditActionType.SparkBalanceStuck && a.ObjectType == AuditObjectType.Wallet && a.ObjectId == "1");

        _time.Advance(TimeSpan.FromHours(1));
        (await _guardrails.CheckAsync([])).Should().BeEmpty();
    }

    [Fact]
    public async Task SatsOfASwapInFlight_AreNotStuck()
    {
        Holds(A, 500_000);

        await _guardrails.CheckAsync([PaidSparkSwap(_time.GetUtcNow())]);

        _guardrails.StuckSince(A).Should().BeNull();
    }

    [Fact]
    public async Task AnotherWalletsSwapInFlight_DoesNotCoverAWalletsSats()
    {
        Wallets(new SparkWalletEntry(A, "transit-a", "02aa", 10_000_000), new SparkWalletEntry(B, "transit-b", "02bb", 10_000_000));
        Holds(A, 500_000);
        Holds(B, 42_000);

        await _guardrails.CheckAsync([PaidSparkSwap(_time.GetUtcNow(), identity: "02aa")]);

        _guardrails.StuckSince(A).Should().BeNull();
        _guardrails.StuckSince(B).Should().Be(_time.GetUtcNow());
    }

    [Fact]
    public async Task SatsBeyondWhatTheSwapsInFlightBroughtIn_AreStuck()
    {
        Holds(A, 600_000);

        await _guardrails.CheckAsync([PaidSparkSwap(_time.GetUtcNow(), received: 499_000)]);

        _guardrails.StuckSince(A).Should().Be(_time.GetUtcNow());
    }

    [Fact]
    public async Task AnExitedSwap_StillAccountsForItsLeaves_UntilItCompletes()
    {
        // A sent exit's leaves stay the wallet's until the exit confirms
        Holds(A, 499_000);
        var swap = PaidSparkSwap(_time.GetUtcNow(), received: 499_000, txId: "exit-tx");

        await _guardrails.CheckAsync([swap]);
        _guardrails.StuckSince(A).Should().BeNull();

        swap.Status = SwapOutStatus.Completed;
        await _guardrails.CheckAsync([swap]);
        _guardrails.StuckSince(A).Should().NotBeNull();
    }

    [Fact]
    public void UnpaidAndDueSats_CountOnlyPendingSparkSwaps()
    {
        var now = _time.GetUtcNow();
        SwapOut[] swaps =
        [
            new() { Provider = SwapProvider.Spark, Status = SwapOutStatus.Pending, SatsAmount = 100_000 },
            PaidSparkSwap(now, received: 499_000),
            PaidSparkSwap(now, received: 300_000, txId: "exit-tx"),
            new() { Provider = SwapProvider.Spark, Status = SwapOutStatus.Completed, SatsAmount = 7 },
            new() { Provider = SwapProvider.Loop, Status = SwapOutStatus.Pending, SatsAmount = 9 }
        ];

        SparkGuardrails.UnpaidSats(swaps).Should().Be(100_000);
        SparkGuardrails.DueSats(swaps).Should().Be(899_000);
    }

    [Fact]
    public async Task AnEmptiedWallet_IsNoLongerStuck_AndCanBeAlertedAgain()
    {
        Holds(A, 42_000);
        await _guardrails.CheckAsync([]);
        _time.Advance(SparkGuardrails.StuckAlertAfter);
        await _guardrails.CheckAsync([]);

        Holds(A, 0);
        await _guardrails.CheckAsync([]);
        _guardrails.StuckSince(A).Should().BeNull();

        Holds(A, 7_000);
        await _guardrails.CheckAsync([]);
        _time.Advance(SparkGuardrails.StuckAlertAfter);
        (await _guardrails.CheckAsync([])).Should().ContainSingle();
    }

    [Fact]
    public async Task AWalletNoLongerListed_IsForgotten()
    {
        Holds(A, 42_000);
        await _guardrails.CheckAsync([]);

        Wallets();
        await _guardrails.CheckAsync([]);

        _guardrails.LastBalance(A).Should().BeNull();
        _guardrails.StuckSince(A).Should().BeNull();
    }

    [Theory]
    [InlineData(ExitSpeed.Fast, 6)]
    [InlineData(ExitSpeed.Medium, 12)]
    [InlineData(ExitSpeed.Slow, 24)]
    public async Task APaidSwapWithoutAConfirmedPayout_IsAlertedOnceWhenOverdueForItsExitSpeed(ExitSpeed exitSpeed, int hours)
    {
        _guardrails = Guardrails(exitSpeed);
        var swap = PaidSparkSwap(_time.GetUtcNow());
        Holds(A, 500_000);

        _time.Advance(TimeSpan.FromHours(hours) - TimeSpan.FromMinutes(1));
        (await _guardrails.CheckAsync([swap])).Should().BeEmpty();

        _time.Advance(TimeSpan.FromMinutes(1));
        var alerts = await _guardrails.CheckAsync([swap]);
        alerts.Should().ContainSingle().Which.Should().Match<SparkAlert>(a =>
            a.Action == AuditActionType.SparkExitOverdue && a.ObjectType == AuditObjectType.SwapOut && a.ObjectId == "req-5");

        (await _guardrails.CheckAsync([swap])).Should().BeEmpty();
    }

    [Fact]
    public async Task ASwapWaitingForItsWalletToFill_IsNotOverdue()
    {
        var swap = PaidSparkSwap(_time.GetUtcNow(), received: 499_000);
        swap.SparkTransferId = "transfer-5";
        Holds(A, 499_000);

        _time.Advance(TimeSpan.FromDays(3));

        (await _guardrails.CheckAsync([swap])).Should().BeEmpty();
    }

    [Fact]
    public async Task AnAttemptedExit_IsOverdueCountingFromItsFirstAttempt()
    {
        var swap = PaidSparkSwap(_time.GetUtcNow(), received: 499_000);
        swap.SparkTransferId = "transfer-5";
        swap.SparkExitDatetime = _time.GetUtcNow().AddDays(2);
        Holds(A, 499_000);

        _time.Advance(TimeSpan.FromDays(2) + TimeSpan.FromHours(24) - TimeSpan.FromMinutes(1));
        (await _guardrails.CheckAsync([swap])).Should().BeEmpty();

        _time.Advance(TimeSpan.FromMinutes(1));
        (await _guardrails.CheckAsync([swap])).Should().ContainSingle()
            .Which.Message.Should().Contain("after its exit was first attempted");
    }

    [Fact]
    public async Task AnUnpaidSwap_IsNotAnOverdueExit()
    {
        var swap = PaidSparkSwap(_time.GetUtcNow());
        swap.LightningFeeSats = null;
        _time.Advance(TimeSpan.FromDays(2));

        (await _guardrails.CheckAsync([swap])).Should().BeEmpty();
    }

    [Fact]
    public async Task AWalletThatIsNotReady_IsNotChecked()
    {
        _spark.EnsureReadyAsync(A, Arg.Any<CancellationToken>()).Returns(new SparkWalletStatus(SparkWalletState.Unavailable, Reason: "down"));

        (await _guardrails.CheckAsync([])).Should().BeEmpty();

        await _spark.DidNotReceiveWithAnyArgs().GetBalanceAsync(default, default);
        _guardrails.LastBalance(A).Should().BeNull();
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
