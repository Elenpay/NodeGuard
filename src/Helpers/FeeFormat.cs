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

using NBitcoin;

namespace NodeGuard.Helpers;

/// <summary>
/// How swap fees read on the Swaps page and in the New Swap dialog: the BTC amount with its
/// percentage of the swap, and ppm (plus USD) in the tooltip. A percentage is ppm / 10,000, as on
/// the Rebalances page.
/// </summary>
public static class FeeFormat
{
    /// <summary>"0.07%" for 700 ppm</summary>
    public static string Percent(long ppm) => $"{ppm / 10_000m:0.####}%";

    /// <summary>"0.00000209 BTC · 0.07%"; "–" while the fee is not known</summary>
    public static string Cell(long? feeSats, long? ppm) =>
        feeSats is not { } sats
            ? "–"
            : $"{new Money(sats, MoneyUnit.Satoshi).ToUnit(MoneyUnit.BTC):f8} BTC · {(ppm is { } p ? Percent(p) : "–")}";

    /// <summary>"700 ppm · $1.92"; "Not reported yet" while the fee is not known</summary>
    public static string Tooltip(long? feeSats, long? ppm, decimal? usd) =>
        feeSats is null
            ? "Not reported yet"
            : string.Join(" · ", new[] { ppm is { } p ? $"{p} ppm" : null, usd is { } u ? $"${u:0.00}" : null }
                .Where(part => part is not null));
}
