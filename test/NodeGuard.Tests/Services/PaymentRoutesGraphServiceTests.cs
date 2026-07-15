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
using NodeGuard.Data.Models;

namespace NodeGuard.Services;

public class PaymentRoutesGraphServiceTests
{
    // Mirrors graph_builder._hop_status_for cases from LightningEye.

    [Fact]
    public void HopStatusFor_SuccessfulPayment_AlwaysSuccess()
    {
        var (status, code) = PaymentRoutesGraphService.HopStatusFor(PaymentRouteStatus.Success, 2, 3, "X");
        status.Should().Be("success");
        code.Should().BeNull();
    }

    [Fact]
    public void HopStatusFor_FailedNoSourceIndex_FallsBackToFailed()
    {
        var (status, code) = PaymentRoutesGraphService.HopStatusFor(PaymentRouteStatus.Failed, 0, null, null);
        status.Should().Be("failed");
        code.Should().BeNull();
    }

    // failure_source_index F = 2. dest_pos = hopIndex + 1.
    [Theory]
    [InlineData(0, "ok")]          // dest_pos 1 < 2  → traversed before the failure
    [InlineData(1, "failed_here")] // dest_pos 2 == 2 → broke here
    [InlineData(2, "unreached")]   // dest_pos 3 > 2  → never attempted
    public void HopStatusFor_FailedWithSourceIndex_ClassifiesPerHop(int hopIndex, string expected)
    {
        var (status, code) = PaymentRoutesGraphService.HopStatusFor(PaymentRouteStatus.Failed, hopIndex, 2, "TEMPORARY_CHANNEL_FAILURE");
        status.Should().Be(expected);
        code.Should().Be(expected == "failed_here" ? "TEMPORARY_CHANNEL_FAILURE" : null);
    }
}
