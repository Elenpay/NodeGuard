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

using Lnrpc;
using NodeGuard.Data.Models;

namespace NodeGuard.Services;

/// <summary>
/// Pure LND-gRPC → <see cref="PaymentRoute"/> mapping helpers used by the tracker job.
/// Kept here (with tests) because these two conversions are the easiest things to get
/// silently wrong when porting from LightningEye's REST tracker.
/// </summary>
public static class PaymentRouteMapping
{
    /// <summary>
    /// gRPC <c>Payment.creation_time_ns</c> is in <b>nanoseconds</b> since the unix epoch.
    /// LightningEye's REST tracker read <c>creation_date</c> in <b>seconds</b>; using the
    /// gRPC value as-is (or as seconds) silently dates every payment to 1970.
    /// </summary>
    public static DateTimeOffset CreatedAtFromCreationTimeNs(long creationTimeNs)
        => DateTimeOffset.FromUnixTimeMilliseconds(creationTimeNs / 1_000_000L);

    /// <summary>
    /// Maps the gRPC payment status enum to our terminal status. Non-terminal states
    /// (IN_FLIGHT / INITIATED / UNKNOWN) map to <see cref="PaymentRouteStatus.Unknown"/>;
    /// the tracker skips those, exactly as the Python tracker ignored non-SUCCEEDED/FAILED.
    /// </summary>
    public static PaymentRouteStatus FromLndPaymentStatus(Payment.Types.PaymentStatus status) => status switch
    {
        Payment.Types.PaymentStatus.Succeeded => PaymentRouteStatus.Success,
        Payment.Types.PaymentStatus.Failed => PaymentRouteStatus.Failed,
        _ => PaymentRouteStatus.Unknown
    };
}
