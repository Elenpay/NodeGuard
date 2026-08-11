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

using System.Collections.Concurrent;
using System.Reflection;
using Google.Protobuf.Reflection;
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

    /// <summary>
    /// Maps a single HTLC attempt's status. Note this is <b>per attempt</b>, not per payment:
    /// a SUCCEEDED payment routinely carries FAILED attempts it retried past, and colouring
    /// those from the payment's status paints failed routes green.
    /// </summary>
    public static PaymentRouteAttemptStatus FromLndHtlcStatus(HTLCAttempt.Types.HTLCStatus status) => status switch
    {
        HTLCAttempt.Types.HTLCStatus.Succeeded => PaymentRouteAttemptStatus.Succeeded,
        HTLCAttempt.Types.HTLCStatus.Failed => PaymentRouteAttemptStatus.Failed,
        HTLCAttempt.Types.HTLCStatus.InFlight => PaymentRouteAttemptStatus.InFlight,
        _ => PaymentRouteAttemptStatus.Unknown
    };

    /// <summary>
    /// Renders <c>Failure.code</c> in its protobuf wire spelling (<c>TEMPORARY_CHANNEL_FAILURE</c>)
    /// rather than the generated C# name (<c>TemporaryChannelFailure</c>), because the frontend
    /// shows this string verbatim and operators match it against LND's own logs and docs.
    /// <para>protoc's C# output exposes <c>Descriptor</c> on messages but not on enums, so the
    /// wire name is only reachable through the <see cref="OriginalNameAttribute"/> the generator
    /// stamps on each member. The lookup is cached — this runs once per persisted failed attempt.</para>
    /// </summary>
    public static string? FailureCodeName(Failure? failure)
    {
        if (failure == null)
        {
            return null;
        }

        return FailureCodeNames.GetOrAdd(failure.Code, static code =>
        {
            var member = typeof(Failure.Types.FailureCode).GetField(code.ToString(),
                BindingFlags.Public | BindingFlags.Static);
            return member?.GetCustomAttribute<OriginalNameAttribute>()?.Name ?? code.ToString();
        });
    }

    private static readonly ConcurrentDictionary<Failure.Types.FailureCode, string> FailureCodeNames = new();
}
