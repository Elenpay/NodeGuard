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

using System.Globalization;
using FluentAssertions;

namespace NodeGuard.Helpers;

public class FeeFormatTests : IDisposable
{
    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;

    public FeeFormatTests() => CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

    public void Dispose() => CultureInfo.CurrentCulture = _culture;

    [Fact]
    public void AFee_ReadsAsBtcAndPercentOfTheSwap()
    {
        FeeFormat.Cell(209, 696).Should().Be("0.00000209 BTC · 0.0696%");
        FeeFormat.Cell(0, 0).Should().Be("0.00000000 BTC · 0%");
        FeeFormat.Percent(2_500).Should().Be("0.25%");
    }

    [Fact]
    public void AnUnknownFee_ReadsAsADash()
    {
        FeeFormat.Cell(null, null).Should().Be("–");
        FeeFormat.Tooltip(null, null, null).Should().Be("Not reported yet");
    }

    [Fact]
    public void TheTooltip_HasPpmAndUsdWhenKnown()
    {
        FeeFormat.Tooltip(209, 696, 1.92m).Should().Be("696 ppm · $1.92");
        FeeFormat.Tooltip(209, 696, null).Should().Be("696 ppm");
    }
}
