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
using Lnrpc;
using NodeGuard.Data.Models;

namespace NodeGuard.Services;

public class PaymentRouteMappingTests
{
    [Fact]
    public void CreatedAtFromCreationTimeNs_TreatsValueAsNanoseconds()
    {
        // 2023-11-14T22:13:20Z = 1_700_000_000 s. gRPC gives that as ns (×1e9).
        const long seconds = 1_700_000_000L;
        var creationTimeNs = seconds * 1_000_000_000L;

        var result = PaymentRouteMapping.CreatedAtFromCreationTimeNs(creationTimeNs);

        result.Should().Be(DateTimeOffset.FromUnixTimeSeconds(seconds));
        result.Year.Should().Be(2023); // guards against the silent 1970 shift
    }

    [Theory]
    [InlineData(Payment.Types.PaymentStatus.Succeeded, PaymentRouteStatus.Success)]
    [InlineData(Payment.Types.PaymentStatus.Failed, PaymentRouteStatus.Failed)]
    [InlineData(Payment.Types.PaymentStatus.InFlight, PaymentRouteStatus.Unknown)]
    [InlineData(Payment.Types.PaymentStatus.Initiated, PaymentRouteStatus.Unknown)]
    public void FromLndPaymentStatus_MapsTerminalStatesAndSkipsTransient(
        Payment.Types.PaymentStatus lnd, PaymentRouteStatus expected)
    {
        PaymentRouteMapping.FromLndPaymentStatus(lnd).Should().Be(expected);
    }

    [Theory]
    [InlineData(HTLCAttempt.Types.HTLCStatus.Succeeded, PaymentRouteAttemptStatus.Succeeded)]
    [InlineData(HTLCAttempt.Types.HTLCStatus.Failed, PaymentRouteAttemptStatus.Failed)]
    [InlineData(HTLCAttempt.Types.HTLCStatus.InFlight, PaymentRouteAttemptStatus.InFlight)]
    public void FromLndHtlcStatus_MapsEveryAttemptState(
        HTLCAttempt.Types.HTLCStatus lnd, PaymentRouteAttemptStatus expected)
    {
        PaymentRouteMapping.FromLndHtlcStatus(lnd).Should().Be(expected);
    }

    [Fact]
    public void FromLndHtlcStatus_NeverProducesUnknown_WhichIsReservedForLegacyRows()
    {
        // Unknown drives the graph's payment-level fallback. If the tracker could emit it for
        // a live attempt, fresh rows would silently take the legacy colouring path.
        foreach (var status in Enum.GetValues<HTLCAttempt.Types.HTLCStatus>())
        {
            PaymentRouteMapping.FromLndHtlcStatus(status).Should().NotBe(PaymentRouteAttemptStatus.Unknown);
        }
    }

    [Fact]
    public void FailureCodeName_UsesProtobufWireSpelling_NotTheCSharpName()
    {
        var failure = new Failure { Code = Failure.Types.FailureCode.TemporaryChannelFailure };

        PaymentRouteMapping.FailureCodeName(failure).Should().Be("TEMPORARY_CHANNEL_FAILURE");
    }

    [Fact]
    public void FailureCodeName_NullFailure_IsNull()
    {
        PaymentRouteMapping.FailureCodeName(null).Should().BeNull();
    }
}
