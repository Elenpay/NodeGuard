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

namespace NodeGuard.Data.Models;

public class SwapOutTests
{
    [Fact]
    public void EachFee_IsAlsoReadInPpmOfTheAmount()
    {
        var swap = new SwapOut { SatsAmount = 300_000, ServiceFeeSats = 209, LightningFeeSats = 12, OnChainFeeSats = null };

        swap.ServiceFeePpm.Should().Be(696);
        swap.LightningFeePpm.Should().Be(40);
        swap.OnChainFeePpm.Should().BeNull("an unknown fee has no rate");
        swap.TotalFeesPpm.Should().Be(736);
    }

    [Fact]
    public void WithNoFeeKnown_OrNoAmount_ThereIsNoRate()
    {
        new SwapOut { SatsAmount = 300_000 }.TotalFeesPpm.Should().BeNull();
        new SwapOut { SatsAmount = 0, ServiceFeeSats = 10 }.ServiceFeePpm.Should().BeNull();
        SwapOut.FeePpm(0, 300_000).Should().Be(0);
    }
}
