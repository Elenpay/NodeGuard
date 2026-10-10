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

using NodeGuard.Data.Models;

namespace NodeGuard.Data.Repositories.Interfaces;

public interface IForwardingHtlcEventRepository
{
    Task<(bool, string?)> UpsertAsync(ForwardingHtlcEvent forwardingHtlcEvent);

    /// <summary>
    /// Forwards the managed node refused for lack of local outbound, ordered by event time.
    /// Narrow by design: only LinkFail/INSUFFICIENT_BALANCE forwards, which are the only failures a
    /// channel open can remedy.
    /// </summary>
    Task<List<ForwardingHtlcFailure>> GetInsufficientBalanceFailures(string managedNodePubKey, DateTimeOffset since);

    /// <summary>
    /// Σ OutgoingAmountMsat of succeeded forwards where OutgoingChannelId == chanIdLnd, for the
    /// given managed node, since the window start (drains our local balance — "push").
    /// </summary>
    Task<long> GetOutgoingAmountMsat(string managedNodePubKey, ulong chanIdLnd, DateTimeOffset since);

    /// <summary>
    /// Same as <see cref="GetOutgoingAmountMsat"/> but for every channel at once, keyed by scid.
    /// Callers needing more than one channel must use this: the per-channel overload re-scans the
    /// node's whole settled window each time, since OutgoingChannelId is not indexed.
    /// </summary>
    Task<Dictionary<ulong, long>> GetOutgoingAmountsMsatByChannel(string managedNodePubKey, DateTimeOffset since);

    /// <summary>Per-channel counterpart of <see cref="GetIncomingAmountMsat"/>, keyed by scid.</summary>
    Task<Dictionary<ulong, long>> GetIncomingAmountsMsatByChannel(string managedNodePubKey, DateTimeOffset since);

    /// <summary>
    /// Σ IncomingAmountMsat of succeeded forwards where IncomingChannelId == chanIdLnd, for the
    /// given managed node, since the window start (fills our local balance — "pull").
    /// </summary>
    Task<long> GetIncomingAmountMsat(string managedNodePubKey, ulong chanIdLnd, DateTimeOffset since);
}