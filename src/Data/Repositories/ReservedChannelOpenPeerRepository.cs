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

public class ReservedChannelOpenPeerRepository : IReservedChannelOpenPeerRepository
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;
    private readonly ILogger<ReservedChannelOpenPeerRepository> _logger;

    public ReservedChannelOpenPeerRepository(IDbContextFactory<ApplicationDbContext> dbContextFactory,
        ILogger<ReservedChannelOpenPeerRepository> logger)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    public async Task<List<ReservedChannelOpenPeer>> GetByNode(int nodeId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        return await context.ReservedChannelOpenPeers
            .Include(x => x.PeerNode)
            .Where(x => x.NodeId == nodeId)
            .OrderBy(x => x.CreationDatetime)
            .ToListAsync();
    }

    public async Task<(bool, string?)> ReplaceForNode(int nodeId, IReadOnlyList<ReservedChannelOpenPeer> peers)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        try
        {
            var existing = await context.ReservedChannelOpenPeers
                .Where(x => x.NodeId == nodeId)
                .ToListAsync();
            context.ReservedChannelOpenPeers.RemoveRange(existing);

            foreach (var peer in peers)
            {
                var row = new ReservedChannelOpenPeer
                {
                    NodeId = nodeId,
                    PeerNodeId = peer.PeerNodeId,
                    BudgetPct = peer.BudgetPct
                };
                row.SetCreationDatetime();
                row.SetUpdateDatetime();
                await context.ReservedChannelOpenPeers.AddAsync(row);
            }

            // One SaveChanges is one transaction, and EF orders a deleted row before the insert that
            // reuses its unique key.
            await context.SaveChangesAsync();

            return (true, null);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error saving the reserved channel open peers of node {NodeId}", nodeId);
            return (false, e.Message);
        }
    }
}
