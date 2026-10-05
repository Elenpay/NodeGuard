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
    private readonly ISparkWalletService _spark = Substitute.For<ISparkWalletService>();
    private readonly ManualTime _time = new();
    private readonly SparkGuardrails _guardrails;

    public SparkGuardrailsTests()
    {
        _spark.Status.Returns(new SparkWalletStatus(SparkWalletState.Ready, "02spark"));
        Holds(0);
        _guardrails = new SparkGuardrails(new SparkSettings { Enabled = true }, _spark, NullLogger<SparkGuardrails>.Instance, _time);
    }

    private void Holds(long owned, long incoming = 0) =>
        _spark.GetBalanceAsync(Arg.Any<CancellationToken>())
            .Returns(new WalletBalance(new SatsBalance(owned, owned, incoming), [], []));

    private static SwapOut PaidSparkSwap(DateTimeOffset created) => new()
    {
        Id = 5, Provider = SwapProvider.Spark, Status = SwapOutStatus.Pending, ProviderId = "req-5", LightningFeeSats = 3,
        CreationDatetime = created
    };

    [Fact]
    public async Task TheBalance_IsCachedWhenChecked()
    {
        Holds(1_000, incoming: 500);

        await _guardrails.CheckAsync([]);

        _guardrails.LastBalance.Should().BeEquivalentTo(new SparkBalanceSnapshot(1_000, 1_000, 500, _time.GetUtcNow()));
    }

    [Fact]
    public async Task SatsLingeringWithNoSwapInFlight_AreAlertedOnceAfterAnHour()
    {
        Holds(42_000);

        (await _guardrails.CheckAsync([])).Should().BeEmpty();
        _guardrails.LingeringSince.Should().Be(_time.GetUtcNow());

        _time.Advance(SparkGuardrails.LingerAlertAfter);
        var alerts = await _guardrails.CheckAsync([]);
        alerts.Should().ContainSingle().Which.Action.Should().Be(AuditActionType.SparkBalanceLingering);

        _time.Advance(TimeSpan.FromHours(1));
        (await _guardrails.CheckAsync([])).Should().BeEmpty();
    }

    [Fact]
    public async Task SatsOfASwapInFlight_DoNotLinger()
    {
        Holds(500_000);

        await _guardrails.CheckAsync([PaidSparkSwap(_time.GetUtcNow())]);

        _guardrails.LingeringSince.Should().BeNull();
    }

    [Fact]
    public async Task AnEmptiedWallet_StopsLingering_AndCanBeAlertedAgain()
    {
        Holds(42_000);
        await _guardrails.CheckAsync([]);
        _time.Advance(SparkGuardrails.LingerAlertAfter);
        await _guardrails.CheckAsync([]);

        Holds(0);
        await _guardrails.CheckAsync([]);
        _guardrails.LingeringSince.Should().BeNull();

        Holds(7_000);
        await _guardrails.CheckAsync([]);
        _time.Advance(SparkGuardrails.LingerAlertAfter);
        (await _guardrails.CheckAsync([])).Should().ContainSingle();
    }

    [Fact]
    public async Task APaidSwapWithoutAConfirmedPayout_IsAlertedOnceWhenOverdue()
    {
        var swap = PaidSparkSwap(_time.GetUtcNow());
        Holds(500_000);

        (await _guardrails.CheckAsync([swap])).Should().BeEmpty();

        _time.Advance(SparkGuardrails.ExitOverdueAfter);
        var alerts = await _guardrails.CheckAsync([swap]);
        alerts.Should().ContainSingle().Which.Should().Match<SparkAlert>(a =>
            a.Action == AuditActionType.SparkExitOverdue && a.ObjectType == AuditObjectType.SwapOut && a.ObjectId == "req-5");

        (await _guardrails.CheckAsync([swap])).Should().BeEmpty();
    }

    [Fact]
    public async Task AnUnpaidSwap_IsNotAnOverdueExit()
    {
        var swap = PaidSparkSwap(_time.GetUtcNow());
        swap.LightningFeeSats = null;
        _time.Advance(SparkGuardrails.ExitOverdueAfter * 2);

        (await _guardrails.CheckAsync([swap])).Should().BeEmpty();
    }

    [Fact]
    public async Task WhenSparkIsNotReady_NothingIsChecked()
    {
        _spark.Status.Returns(new SparkWalletStatus(SparkWalletState.Unavailable, Reason: "down"));

        (await _guardrails.CheckAsync([])).Should().BeEmpty();

        await _spark.DidNotReceiveWithAnyArgs().GetBalanceAsync(default);
        _guardrails.LastBalance.Should().BeNull();
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
