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

public interface IAuditLogRepository
{
    /// <summary>
    /// Add a new audit log entry
    /// </summary>
    Task<(bool, string?)> AddAsync(AuditLog auditLog);

    /// <summary>
    /// Get paginated audit logs with optional filtering
    /// </summary>
    Task<(List<AuditLog>, int)> GetPaginatedAsync(
        int page,
        int pageSize,
        AuditActionType? actionType = null,
        AuditEventType? eventType = null,
        AuditObjectType? objectType = null,
        string? userId = null,
        DateTimeOffset? fromDate = null,
        DateTimeOffset? toDate = null);

    /// <summary>
    /// Delete audit logs older than the specified date
    /// </summary>
    Task<int> DeleteOlderThanAsync(DateTimeOffset cutoffDate);
}
