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
using NodeGuard.Helpers;

namespace NodeGuard.Tests;

public class BlockHeightHelperTests
{
    // scid = (blockHeight << 40) | (txIndex << 16) | outputIndex
    private static ulong Scid(uint blockHeight, uint txIndex = 3, ushort outputIndex = 1)
        => ((ulong)blockHeight << 40) | ((ulong)txIndex << 16) | outputIndex;

    [Fact]
    public void FundingHeight_ParsesBlockHeightComponent()
    {
        BlockHeightHelper.FundingHeightFromChanId(Scid(800_000), chainTip: 800_010)
            .Should().Be(800_000);
    }

    [Fact]
    public void AgeBlocks_IsChainTipMinusFundingHeight()
    {
        BlockHeightHelper.AgeBlocksFromChanId(Scid(800_000), chainTip: 800_010)
            .Should().Be(10);
    }

    [Fact]
    public void ZeroChanId_ReturnsNull()
    {
        BlockHeightHelper.FundingHeightFromChanId(0, chainTip: 800_010).Should().BeNull();
        BlockHeightHelper.AgeBlocksFromChanId(0, chainTip: 800_010).Should().BeNull();
    }

    [Fact]
    public void AliasScid_AboveChainTip_ReturnsNull()
    {
        // LND alias range encodes heights far above any real chain tip.
        var alias = Scid(16_000_000);
        BlockHeightHelper.FundingHeightFromChanId(alias, chainTip: 800_010).Should().BeNull();
        BlockHeightHelper.AgeBlocksFromChanId(alias, chainTip: 800_010).Should().BeNull();
    }
}
