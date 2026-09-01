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

public class ChannelFeeStateRepository : IChannelFeeStateRepository
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;

    public ChannelFeeStateRepository(IDbContextFactory<ApplicationDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    public async Task<ChannelFeeState?> GetByChannelIdAndNode(int channelId, string managedNodePubKey)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        return await context.ChannelFeeStates
            .FirstOrDefaultAsync(x => x.ChannelId == channelId && x.ManagedNodePubKey == managedNodePubKey);
    }

    public async Task<List<ChannelFeeState>> GetByManagedNodePubKey(string managedNodePubKey)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        return await context.ChannelFeeStates
            .Where(x => x.ManagedNodePubKey == managedNodePubKey)
            .ToListAsync();
    }

    public async Task UpsertByChannelAndNode(ChannelFeeState state)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        var existing = await context.ChannelFeeStates
            .FirstOrDefaultAsync(x => x.ChannelId == state.ChannelId
                                      && x.ManagedNodePubKey == state.ManagedNodePubKey);

        if (existing == null)
        {
            state.Channel = null!;
            state.SetCreationDatetime();
            state.SetUpdateDatetime();
            await context.ChannelFeeStates.AddAsync(state);
        }
        else
        {
            existing.LastFeeUpdateAt = state.LastFeeUpdateAt;
            existing.LastAppliedOutboundBaseFeeMsat = state.LastAppliedOutboundBaseFeeMsat;
            existing.LastAppliedOutboundPpm = state.LastAppliedOutboundPpm;
            existing.LastAppliedInboundBaseMsat = state.LastAppliedInboundBaseMsat;
            existing.LastAppliedInboundPpm = state.LastAppliedInboundPpm;
            existing.LastComputedTarget = state.LastComputedTarget;
            existing.LastObservedRatio = state.LastObservedRatio;
            existing.SetUpdateDatetime();
        }

        await context.SaveChangesAsync();
    }

    public async Task<bool> DeleteByChannelId(int channelId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        var existing = await context.ChannelFeeStates
            .Where(x => x.ChannelId == channelId)
            .ToListAsync();

        if (existing.Count == 0)
        {
            return false;
        }

        context.ChannelFeeStates.RemoveRange(existing);
        await context.SaveChangesAsync();
        return true;
    }

    public async Task<bool> DeleteByManagedNodePubKey(string managedNodePubKey)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        var states = await context.ChannelFeeStates
            .Where(x => x.ManagedNodePubKey == managedNodePubKey)
            .ToListAsync();

        if (states.Count == 0)
        {
            return false;
        }

        context.ChannelFeeStates.RemoveRange(states);
        await context.SaveChangesAsync();
        return true;
    }
}
