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

using NodeGuard.Data.Repositories.Interfaces;
using Quartz;

namespace NodeGuard.Jobs;

/// <summary>
/// Job that cleans up old audit log entries based on the configured retention policy
/// </summary>
[DisallowConcurrentExecution]
public class AuditLogCleanupJob : IJob
{
    private readonly IAuditLogRepository _auditLogRepository;
    private readonly ILogger<AuditLogCleanupJob> _logger;

    public AuditLogCleanupJob(IAuditLogRepository auditLogRepository, ILogger<AuditLogCleanupJob> logger)
    {
        _auditLogRepository = auditLogRepository;
        _logger = logger;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        _logger.LogInformation("Starting audit log cleanup job");

        try
        {
            var retentionDays = Constants.AUDIT_LOG_RETENTION_DAYS;
            var cutoffDate = DateTimeOffset.UtcNow.AddDays(-retentionDays);

            _logger.LogInformation("Deleting audit logs older than {CutoffDate} (retention: {RetentionDays} days)", 
                cutoffDate, retentionDays);

            var deletedCount = await _auditLogRepository.DeleteOlderThanAsync(cutoffDate);

            _logger.LogInformation("Audit log cleanup completed. Deleted {DeletedCount} entries", deletedCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during audit log cleanup");
            throw;
        }
    }
}
