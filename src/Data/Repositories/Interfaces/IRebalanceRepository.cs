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
using NodeGuard.Data.Models;

namespace NodeGuard.Data.Repositories.Interfaces;

public interface IRebalanceRepository
{
    Task<Rebalance?> GetById(int id);

    Task<(List<Rebalance> rebalances, int totalCount)> GetPaginatedAsync(
        int pageNumber,
        int pageSize,
        RebalanceStatus? status = null,
        int? nodeId = null,
        int? sourceChannelId = null,
        string? userId = null,
        bool? isManual = null,
        DateTimeOffset? fromDate = null,
        DateTimeOffset? toDate = null);

    /// <summary>
    /// Returns rebalances the monitor job should reconcile against LND:
    /// non-terminal rows (Pending/InFlight) plus recently-marked terminal failures
    /// within <paramref name="recentTerminalWindow"/>. Only rows with a stored payment hash
    /// are returned — without a hash there is nothing to look up in LND.
    /// The recent-terminal sweep exists because the catch-all in RebalanceService can mark
    /// a row Failed on transient errors (cancellation, RPC timeout) while LND has actually
    /// settled the payment. Includes Node so the caller can call LND directly.
    /// </summary>
    Task<List<Rebalance>> GetReconcilable(TimeSpan recentTerminalWindow);

    Task<(bool, string?)> AddAsync(Rebalance rebalance);

    (bool, string?) Update(Rebalance rebalance);

    /// <summary>
    /// Counts non-terminal rebalances (Pending + InFlight) for a node. Used by the Phase 3
    /// in-flight cap. Declared in Phase 1 alongside the routing-engine repositories.
    /// </summary>
    Task<int> GetInFlightByNode(int nodeId);

    /// <summary>
    /// True when a non-terminal rebalance (Pending/InFlight) has this channel as its source.
    /// The Phase 2 fee engine uses this to enforce the fee-vs-rebalance authority split: while
    /// a rebalance is moving a channel's ratio, the fee engine must not react to that manufactured
    /// signal. <paramref name="sourceChannelId"/> is the <see cref="Channel"/> primary key.
    /// </summary>
    Task<bool> HasInFlightRebalanceBySourceChannel(int sourceChannelId);

    /// <summary>
    /// Budget consumption for a node since <paramref name="since"/> (by CreationDatetime):
    /// non-terminal rows count MAX(FeePaidSats, ReservedFeeSats) so in-flight spend counts
    /// immediately, Succeeded rows count FeePaidSats, other terminal rows count 0.
    /// </summary>
    Task<long> GetConsumedFeesSince(int nodeId, DateTimeOffset since);
}
