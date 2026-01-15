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

namespace NodeGuard.Services;

public interface IAuditService
{
    /// <summary>
    /// Log an audit event with all required fields
    /// </summary>
    Task LogAsync(
        AuditActionType actionType,
        AuditEventType eventType,
        AuditObjectType objectAffected,
        string? objectId = null,
        string? userId = null,
        string? username = null,
        string? ipAddress = null,
        object? details = null);

    /// <summary>
    /// Log an audit event using HttpContext for user and IP extraction
    /// </summary>
    Task LogAsync(
        AuditActionType actionType,
        AuditEventType eventType,
        AuditObjectType objectAffected,
        string? objectId = null,
        object? details = null);

    /// <summary>
    /// Log an audit event from a system process (e.g., cron job) without HTTP context
    /// </summary>
    Task LogSystemAsync(
        AuditActionType actionType,
        AuditEventType eventType,
        AuditObjectType objectAffected,
        string? objectId = null,
        object? details = null);
}
