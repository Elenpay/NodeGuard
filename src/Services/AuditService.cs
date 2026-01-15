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

using System.Security.Claims;
using System.Text.Json;
using NodeGuard.Data.Models;
using NodeGuard.Data.Repositories.Interfaces;
using NodeGuard.Helpers;

namespace NodeGuard.Services;

public class AuditService : IAuditService
{
    private readonly IAuditLogRepository _auditLogRepository;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<AuditService> _logger;

    public AuditService(
        IAuditLogRepository auditLogRepository,
        IHttpContextAccessor httpContextAccessor,
        ILogger<AuditService> logger)
    {
        _auditLogRepository = auditLogRepository;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    public async Task LogAsync(
        AuditActionType actionType,
        AuditEventType eventType,
        AuditObjectType objectAffected,
        string? objectId = null,
        string? userId = null,
        string? username = null,
        string? ipAddress = null,
        object? details = null)
    {
        try
        {
            var detailsJson = details != null ? JsonSerializer.Serialize(details) : null;
            
            // Log to system logger
            _logger.LogInformation(
                "AUDIT: Action={ActionType} Event={EventType} Object={ObjectType} ObjectId={ObjectId} User={Username} UserId={UserId} IP={IpAddress} Details={Details}",
                actionType,
                eventType,
                objectAffected,
                objectId ?? "N/A",
                username ?? "N/A",
                userId ?? "N/A",
                ipAddress ?? "N/A",
                detailsJson ?? "N/A");

            var auditLog = new AuditLog
            {
                ActionType = actionType,
                EventType = eventType,
                ObjectAffected = objectAffected,
                ObjectId = objectId,
                UserId = userId,
                Username = username,
                IpAddress = ipAddress,
                Details = detailsJson
            };

            var (success, error) = await _auditLogRepository.AddAsync(auditLog);
            
            if (!success)
            {
                _logger.LogError("Failed to persist audit log to database: {Error}", error);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error logging audit event");
        }
    }

    public async Task LogAsync(
        AuditActionType actionType,
        AuditEventType eventType,
        AuditObjectType objectAffected,
        string? objectId = null,
        object? details = null)
    {
        var (userId, username, ipAddress) = ExtractContextInfo();
        
        await LogAsync(
            actionType,
            eventType,
            objectAffected,
            objectId,
            userId,
            username,
            ipAddress,
            details);
    }

    public async Task LogSystemAsync(
        AuditActionType actionType,
        AuditEventType eventType,
        AuditObjectType objectAffected,
        string? objectId = null,
        object? details = null)
    {
        await LogAsync(
            actionType,
            eventType,
            objectAffected,
            objectId,
            null, // No user ID for system operations
            "SYSTEM",
            null, // No IP address for system operations
            details);
    }

    private (string? UserId, string? Username, string? IpAddress) ExtractContextInfo()
    {
        string? userId = null;
        string? username = null;
        string? ipAddress = null;

        try
        {
            var httpContext = _httpContextAccessor.HttpContext;
            
            if (httpContext != null)
            {
                // Extract user info from claims
                var user = httpContext.User;
                if (user?.Identity?.IsAuthenticated == true)
                {
                    userId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                    username = user.FindFirst(ClaimTypes.Name)?.Value 
                               ?? user.FindFirst(ClaimTypes.Email)?.Value
                               ?? user.Identity.Name;
                }

                // Extract IP address (considering proxies)
                ipAddress = httpContext.GetClientIpAddress();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error extracting context info for audit log");
        }

        return (userId, username, ipAddress);
    }
}
