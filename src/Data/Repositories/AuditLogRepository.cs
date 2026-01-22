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

public class AuditLogRepository : IAuditLogRepository
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;
    private readonly ILogger<AuditLogRepository> _logger;

    public AuditLogRepository(IDbContextFactory<ApplicationDbContext> dbContextFactory, ILogger<AuditLogRepository> logger)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    public async Task<(bool, string?)> AddAsync(AuditLog auditLog)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();

        try
        {
            auditLog.Timestamp = DateTimeOffset.UtcNow;
            await dbContext.AuditLogs.AddAsync(auditLog);
            await dbContext.SaveChangesAsync();
            return (true, null);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error adding audit log entry");
            return (false, e.Message);
        }
    }

    public async Task<(List<AuditLog>, int)> GetPaginatedAsync(
        int page,
        int pageSize,
        AuditActionType? actionType = null,
        AuditEventType? eventType = null,
        AuditObjectType? objectType = null,
        string? userId = null,
        DateTimeOffset? fromDate = null,
        DateTimeOffset? toDate = null)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();

        var query = dbContext.AuditLogs.AsQueryable();

        if (actionType.HasValue)
            query = query.Where(a => a.ActionType == actionType.Value);

        if (eventType.HasValue)
            query = query.Where(a => a.EventType == eventType.Value);

        if (objectType.HasValue)
            query = query.Where(a => a.ObjectAffected == objectType.Value);

        if (!string.IsNullOrEmpty(userId))
            query = query.Where(a => a.UserId == userId);

        if (fromDate.HasValue)
            query = query.Where(a => a.Timestamp >= fromDate.Value);

        if (toDate.HasValue)
            query = query.Where(a => a.Timestamp <= toDate.Value);

        var totalCount = await query.CountAsync();

        var results = await query
            .OrderByDescending(a => a.Timestamp)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return (results, totalCount);
    }

    public async Task<int> DeleteOlderThanAsync(DateTimeOffset cutoffDate)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();

        var count = await dbContext.AuditLogs
            .Where(a => a.Timestamp < cutoffDate)
            .ExecuteDeleteAsync();

        _logger.LogInformation("Deleted {Count} audit log entries older than {CutoffDate}", count, cutoffDate);

        return count;
    }
}
