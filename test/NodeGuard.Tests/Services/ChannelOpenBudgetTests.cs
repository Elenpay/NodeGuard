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

namespace NodeGuard.Services;

public class ChannelOpenBudgetTests
{
    private const string Loop = "03loop";
    private const string Other = "03other";

    private static ChannelOpenBudget Build(long budgetSats, int loopPct = 0, Dictionary<string, long>? committed = null)
        => ChannelOpenBudget.Build(
            budgetSats,
            loopPct > 0 ? new Dictionary<string, int> { [Loop] = loopPct } : new Dictionary<string, int>(),
            committed ?? new Dictionary<string, long>());

    [Fact]
    public void Build_NoReservedPeers_LeavesTheBudgetMinusWhatWasCommitted()
    {
        var budget = Build(10_000_000, committed: new() { [Other] = 3_000_000, [""] = 1_000_000 });

        budget.HeadroomFor(Other).Should().Be(6_000_000);
        budget.IsReserved(Other).Should().BeFalse();
    }

    [Fact]
    public void Build_ReservedShare_IsOutOfReachOfOtherPeers()
    {
        var budget = Build(10_000_000, loopPct: 30);

        budget.HeadroomFor(Other).Should().Be(7_000_000);
        budget.HeadroomFor(Loop).Should().Be(10_000_000);
    }

    [Fact]
    public void Build_ReservedPeerOverItsShare_TookTheExcessFromTheRest()
    {
        var budget = Build(10_000_000, loopPct: 30, committed: new() { [Loop] = 4_000_000 });

        budget.HeadroomFor(Loop).Should().Be(6_000_000);
        budget.HeadroomFor(Other).Should().Be(6_000_000);
    }

    [Fact]
    public void Build_OtherPeersOverspent_StillLeaveTheReservedShare()
    {
        // Opens made by hand can overshoot what is left for other peers; the reserved share survives.
        var budget = Build(10_000_000, loopPct: 30, committed: new() { [Other] = 9_000_000 });

        budget.HeadroomFor(Other).Should().Be(0);
        budget.HeadroomFor(Loop).Should().Be(3_000_000);
        budget.IsExhausted.Should().BeFalse();
    }

    [Fact]
    public void Spend_ReservedPeer_DrawsOnItsShareBeforeTheRest()
    {
        var budget = Build(10_000_000, loopPct: 30);

        budget.Spend(Loop, 2_000_000);
        budget.HeadroomFor(Other).Should().Be(7_000_000);

        budget.Spend(Loop, 2_000_000);
        budget.HeadroomFor(Other).Should().Be(6_000_000);
        budget.HeadroomFor(Loop).Should().Be(6_000_000);
    }

    [Fact]
    public void Spend_OtherPeer_NeverTouchesTheReservedShare()
    {
        var budget = Build(10_000_000, loopPct: 30);

        budget.Spend(Other, 7_000_000);

        budget.HeadroomFor(Other).Should().Be(0);
        budget.HeadroomFor(Loop).Should().Be(3_000_000);
    }

    [Fact]
    public void IsExhausted_OnlyOnceEveryPartIsSpent()
    {
        var budget = Build(10_000_000, loopPct: 30, committed: new() { [Other] = 7_000_000 });
        budget.IsExhausted.Should().BeFalse();

        budget.Spend(Loop, 3_000_000);

        budget.IsExhausted.Should().BeTrue();
    }
}
