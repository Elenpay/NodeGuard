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

public class ChannelRoutingStateRepository : IChannelRoutingStateRepository
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;

    public ChannelRoutingStateRepository(IDbContextFactory<ApplicationDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    public async Task<ChannelRoutingState?> GetByChannelIdAndNode(int channelId, string managedNodePubKey)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        return await context.ChannelRoutingStates
            .FirstOrDefaultAsync(x => x.ChannelId == channelId && x.ManagedNodePubKey == managedNodePubKey);
    }

    public async Task<List<ChannelRoutingState>> GetByManagedNodePubKey(string managedNodePubKey)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        return await context.ChannelRoutingStates
            .Where(x => x.ManagedNodePubKey == managedNodePubKey)
            .ToListAsync();
    }

    public async Task UpsertByChannelAndNode(ChannelRoutingState state)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        var existing = await context.ChannelRoutingStates
            .FirstOrDefaultAsync(x => x.ChannelId == state.ChannelId
                                      && x.ManagedNodePubKey == state.ManagedNodePubKey);

        if (existing == null)
        {
            // Insert via FK only — never let the Channel nav try to insert/attach a Channel.
            state.Channel = null!;
            state.SetCreationDatetime();
            state.SetUpdateDatetime();
            await context.ChannelRoutingStates.AddAsync(state);
        }
        else
        {
            existing.ChanIdLnd = state.ChanIdLnd;
            existing.TargetLocalRatio = state.TargetLocalRatio;
            existing.PeerFlowCategory = state.PeerFlowCategory;
            existing.PendingCategory = state.PendingCategory;
            existing.ConsecutiveCategoryCyclesInNewState = state.ConsecutiveCategoryCyclesInNewState;
            existing.FundingBlockHeight = state.FundingBlockHeight;
            existing.AgeBlocks = state.AgeBlocks;
            existing.EmaLocalRatio = state.EmaLocalRatio;
            existing.PushMsatWindow = state.PushMsatWindow;
            existing.PullMsatWindow = state.PullMsatWindow;
            existing.NetFlowRatio = state.NetFlowRatio;
            existing.PeerInitiated = state.PeerInitiated;
            existing.LastKnownNumUpdates = state.LastKnownNumUpdates;
            existing.LastKnownLifetime = state.LastKnownLifetime;
            existing.LastKnownUptime = state.LastKnownUptime;
            existing.LastCategorizedAt = state.LastCategorizedAt;
            existing.LastEvaluatedAt = state.LastEvaluatedAt;
            existing.SetUpdateDatetime();
        }

        await context.SaveChangesAsync();
    }
}
