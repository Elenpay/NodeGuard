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

using Microsoft.EntityFrameworkCore;
using NodeGuard.Data.Models;
using NodeGuard.Data.Repositories.Interfaces;

namespace NodeGuard.Data.Repositories;

public class ChannelFlowAnalyticsRepository : IChannelFlowAnalyticsRepository
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;

    public ChannelFlowAnalyticsRepository(IDbContextFactory<ApplicationDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    public async Task<long> GetOutgoingAmountMsat(string managedNodePubKey, ulong chanIdLnd, DateTimeOffset since)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        return await Settled(context, managedNodePubKey, since)
            .Where(x => x.OutgoingChannelId == chanIdLnd)
            .SumAsync(x => (long?)x.OutgoingAmountMsat) ?? 0;
    }

    public async Task<long> GetIncomingAmountMsat(string managedNodePubKey, ulong chanIdLnd, DateTimeOffset since)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        return await Settled(context, managedNodePubKey, since)
            .Where(x => x.IncomingChannelId == chanIdLnd)
            .SumAsync(x => (long?)x.IncomingAmountMsat) ?? 0;
    }

    public async Task<long> GetOrganicFeesEarnedMsat(string managedNodePubKey, ulong chanIdLnd, DateTimeOffset since)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        return await Settled(context, managedNodePubKey, since)
            .Where(x => x.OutgoingChannelId == chanIdLnd)
            .SumAsync(x => x.FeeMsat) ?? 0;
    }

    /// <summary>
    /// Base query shared by every method: succeeded (Settled) forwards for the given managed
    /// node within the window. No caller ever reads non-Settled rows.
    /// </summary>
    private static IQueryable<ForwardingHtlcEvent> Settled(
        ApplicationDbContext context, string managedNodePubKey, DateTimeOffset since)
    {
        return context.ForwardingHtlcEvents
            .Where(x => x.ManagedNodePubKey == managedNodePubKey
                        && x.Outcome == ForwardingOutcome.Settled
                        && x.EventTimestamp >= since);
    }
}
