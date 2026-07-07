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

namespace NodeGuard.Helpers;

public static class BlockHeightHelper
{
    /// <summary>
    /// Parse funding block height (bits 63..40) from scid. Returns null for pending,
    /// alias, zero-conf, or malformed scids.
    /// </summary>
    public static uint? FundingHeightFromChanId(ulong chanId, uint chainTip)
    {
        if (chanId == 0) return null;

        var blockHeight = (uint)(chanId >> 40);
        if (blockHeight == 0 || blockHeight > chainTip) return null;

        return blockHeight;
    }

    /// <summary>
    /// Channel age in blocks (chainTip - funding height), or null if unfunded/aliased/malformed.
    /// </summary>
    public static uint? AgeBlocksFromChanId(ulong chanId, uint chainTip)
    {
        var height = FundingHeightFromChanId(chanId, chainTip);
        return height == null ? null : chainTip - height.Value;
    }
}
