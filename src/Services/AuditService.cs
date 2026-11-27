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
using System.Text.Json.Nodes;
using NodeGuard.Data.Models;
using NodeGuard.Data.Repositories.Interfaces;
using NodeGuard.Helpers;

namespace NodeGuard.Services;

public class AuditService : IAuditService
{
    private const string ApiTokenIdKey = "ApiTokenId";

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
        object? details = null)
    {
        var (userId, username, ipAddress, contextDetails) = ExtractContextInfo();
        await LogInternalAsync(actionType, eventType, objectAffected, objectId, userId, username, ipAddress, details,
            contextDetails);
    }

    public async Task LogSystemAsync(
        AuditActionType actionType,
        AuditEventType eventType,
        AuditObjectType objectAffected,
        string? objectId = null,
        object? details = null)
    {
        await LogInternalAsync(actionType, eventType, objectAffected, objectId, null, "SYSTEM", null, details);
    }

    private async Task LogInternalAsync(
        AuditActionType actionType,
        AuditEventType eventType,
        AuditObjectType objectAffected,
        string? objectId,
        string? userId,
        string? username,
        string? ipAddress,
        object? details,
        IReadOnlyDictionary<string, JsonNode?>? contextDetails = null)
    {
        try
        {
            var detailsJson = SerializeDetails(details, contextDetails);
            
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

    /// <summary>
    /// Serializes the details, adding the request context (the user's roles at the time of the action, or the API
    /// token that authenticated a gRPC call) so it can be checked later even if roles or tokens change. Details that
    /// are not a JSON object are wrapped under <c>Details</c> and keys supplied by the caller are never overwritten.
    /// The context is recorded even when the caller passes no details, so every user audit keeps its roles.
    /// </summary>
    private static string? SerializeDetails(object? details, IReadOnlyDictionary<string, JsonNode?>? contextDetails)
    {
        if (contextDetails == null || contextDetails.Count == 0)
        {
            return details == null ? null : JsonSerializer.Serialize(details);
        }

        var node = details != null ? JsonSerializer.SerializeToNode(details) : new JsonObject();
        var detailsObject = node as JsonObject ?? new JsonObject { ["Details"] = node };

        foreach (var (key, value) in contextDetails)
        {
            if (!detailsObject.ContainsKey(key))
            {
                detailsObject[key] = value?.DeepClone();
            }
        }

        return detailsObject.ToJsonString();
    }

    /// <summary>
    /// Token names have no length limit, so the name is cut to keep the username within <see cref="AuditLog.Username"/>'s
    /// 256 characters; otherwise the insert fails and the audit is lost.
    /// </summary>
    private static string FormatApiTokenUsername(AuthenticatedApiToken apiToken)
    {
        const int usernameMaxLength = 256;
        var prefix = $"API token {apiToken.Id} (";
        var maxNameLength = usernameMaxLength - prefix.Length - 1;
        var name = apiToken.Name.Length > maxNameLength ? apiToken.Name[..maxNameLength] : apiToken.Name;
        return $"{prefix}{name})";
    }

    private (string? UserId, string? Username, string? IpAddress, IReadOnlyDictionary<string, JsonNode?> ContextDetails)
        ExtractContextInfo()
    {
        string? userId = null;
        string? username = null;
        string? ipAddress = null;
        var contextDetails = new Dictionary<string, JsonNode?>();

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
                    contextDetails["UserRoles"] = new JsonArray(user.FindAll(ClaimTypes.Role)
                        .Select(c => (JsonNode?)JsonValue.Create(c.Value)).ToArray());
                }

                // gRPC calls are authenticated by an API token instead of a user (see GRPCAuthInterceptor)
                if (httpContext.Items.TryGetValue(AuthenticatedApiToken.HttpContextItemKey, out var item)
                    && item is AuthenticatedApiToken apiToken)
                {
                    // The id keeps tokens sharing a name apart, and the spaces keep it from passing for a username
                    username ??= FormatApiTokenUsername(apiToken);
                    contextDetails[ApiTokenIdKey] = apiToken.Id;
                    contextDetails["ApiTokenName"] = apiToken.Name;
                }

                // Extract IP address (considering proxies)
                ipAddress = httpContext.GetClientIpAddress();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error extracting context info for audit log");
        }

        return (userId, username, ipAddress, contextDetails);
    }
}
