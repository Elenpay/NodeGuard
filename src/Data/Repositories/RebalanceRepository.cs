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
using NBitcoin;
using NodeGuard.Data.Models;
using NodeGuard.Data.Repositories.Interfaces;

namespace NodeGuard.Data.Repositories;

public class RebalanceRepository : IRebalanceRepository
{
    private readonly IRepository<Rebalance> _repository;
    private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;

    public RebalanceRepository(IRepository<Rebalance> repository, IDbContextFactory<ApplicationDbContext> dbContextFactory)
    {
        _repository = repository;
        _dbContextFactory = dbContextFactory;
    }

    public async Task<Rebalance?> GetById(int id)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        return await context.Rebalances
            .Include(r => r.Node)
            .Include(r => r.SourceChannel)
            .Include(r => r.UserRequestor)
            .SingleOrDefaultAsync(r => r.Id == id);
    }

    public async Task<(List<Rebalance> rebalances, int totalCount)> GetPaginatedAsync(
        int pageNumber,
        int pageSize,
        RebalanceStatus? status = null,
        int? nodeId = null,
        int? sourceChannelId = null,
        string? userId = null,
        bool? isManual = null,
        DateTimeOffset? fromDate = null,
        DateTimeOffset? toDate = null)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        var query = context.Rebalances
            .Include(r => r.Node)
            .Include(r => r.SourceChannel)
                .ThenInclude(c => c!.SourceNode)
            .Include(r => r.SourceChannel)
                .ThenInclude(c => c!.DestinationNode)
            .Include(r => r.UserRequestor)
            .AsQueryable();

        if (status.HasValue)
            query = query.Where(r => r.Status == status.Value);

        if (nodeId.HasValue)
            query = query.Where(r => r.NodeId == nodeId.Value);

        if (sourceChannelId.HasValue)
            query = query.Where(r => r.SourceChannelId == sourceChannelId.Value);

        if (!string.IsNullOrEmpty(userId))
            query = query.Where(r => r.UserRequestorId == userId);

        if (isManual.HasValue)
            query = query.Where(r => r.IsManual == isManual.Value);

        if (fromDate.HasValue)
            query = query.Where(r => r.CreationDatetime >= fromDate.Value);

        if (toDate.HasValue)
            query = query.Where(r => r.CreationDatetime <= toDate.Value);

        query = query.OrderByDescending(r => r.CreationDatetime);

        var totalCount = await query.CountAsync();

        var rebalances = await query
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return (rebalances, totalCount);
    }

    public async Task<List<Rebalance>> GetReconcilable(TimeSpan recentTerminalWindow)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        var terminalCutoff = DateTimeOffset.UtcNow - recentTerminalWindow;

        return await context.Rebalances
            .Include(r => r.Node)
            .Where(r => r.PaymentHashHex != null
                        && (r.Status == RebalanceStatus.Pending
                            || r.Status == RebalanceStatus.InFlight
                            || ((r.Status == RebalanceStatus.Failed
                                 || r.Status == RebalanceStatus.Timeout
                                 || r.Status == RebalanceStatus.NoRoute
                                 || r.Status == RebalanceStatus.InsufficientBalance)
                                && r.UpdateDatetime >= terminalCutoff)))
            .ToListAsync();
    }

    public async Task<(bool, string?)> AddAsync(Rebalance rebalance)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        rebalance.SetCreationDatetime();
        rebalance.SetUpdateDatetime();

        return await _repository.AddAsync(rebalance, context);
    }

    public (bool, string?) Update(Rebalance rebalance)
    {
        using var context = _dbContextFactory.CreateDbContext();

        rebalance.SetUpdateDatetime();

        return _repository.Update(rebalance, context);
    }

    public async Task<int> GetInFlightByNode(int nodeId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        return await context.Rebalances
            .CountAsync(r => r.NodeId == nodeId
                             && (r.Status == RebalanceStatus.Pending || r.Status == RebalanceStatus.InFlight));
    }

    public async Task<long> GetConsumedFeesSince(int nodeId, DateTimeOffset since)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        // Only rows that can have consumed budget: in-flight (reserved or partially paid) and
        // settled. Other terminal states (Failed/NoRoute/Timeout/…) paid nothing → excluded.
        var rows = await context.Rebalances
            .Where(r => r.NodeId == nodeId
                        && r.CreationDatetime >= since
                        && (r.Status == RebalanceStatus.Pending
                            || r.Status == RebalanceStatus.InFlight
                            || r.Status == RebalanceStatus.Succeeded))
            .Select(r => new { r.Status, r.FeePaidSats, r.ReservedFeeSats })
            .ToListAsync();

        long total = 0;
        foreach (var r in rows)
        {
            var feePaid = r.FeePaidSats ?? 0;
            total += r.Status == RebalanceStatus.Succeeded
                ? feePaid
                : Math.Max(feePaid, r.ReservedFeeSats ?? 0); // in-flight spend counts immediately
        }

        return total;
    }
}
