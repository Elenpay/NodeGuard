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

using FluentAssertions;
using Microsoft.Extensions.Logging;
using NodeGuard.Data.Repositories.Interfaces;
using Quartz;

namespace NodeGuard.Jobs;

public class AuditLogCleanupJobTests
{
    private Mock<IAuditLogRepository> _auditLogRepositoryMock;
    private Mock<ILogger<AuditLogCleanupJob>> _loggerMock;
    private AuditLogCleanupJob _auditLogCleanupJob;
    private Mock<IJobExecutionContext> _jobExecutionContextMock;

    public AuditLogCleanupJobTests()
    {
        _auditLogRepositoryMock = new Mock<IAuditLogRepository>();
        _loggerMock = new Mock<ILogger<AuditLogCleanupJob>>();
        _auditLogCleanupJob = new AuditLogCleanupJob(
            _auditLogRepositoryMock.Object,
            _loggerMock.Object
        );
        _jobExecutionContextMock = new Mock<IJobExecutionContext>();
    }

    [Fact]
    public async Task Execute_SuccessfulCleanup_DeletesRecordsAndLogsSuccess()
    {
        // Arrange
        var deletedCount = 150;
        _auditLogRepositoryMock
            .Setup(x => x.DeleteOlderThanAsync(It.IsAny<DateTimeOffset>()))
            .ReturnsAsync(deletedCount);

        // Act
        await _auditLogCleanupJob.Execute(_jobExecutionContextMock.Object);

        // Assert
        _auditLogRepositoryMock.Verify(
            x => x.DeleteOlderThanAsync(It.IsAny<DateTimeOffset>()),
            Times.Once);

        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString().Contains("Starting audit log cleanup job")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.Once);

        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString().Contains($"Deleted {deletedCount} entries")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task Execute_NoRecordsToDelete_CompletesSuccessfully()
    {
        // Arrange
        _auditLogRepositoryMock
            .Setup(x => x.DeleteOlderThanAsync(It.IsAny<DateTimeOffset>()))
            .ReturnsAsync(0);

        // Act
        await _auditLogCleanupJob.Execute(_jobExecutionContextMock.Object);

        // Assert
        _auditLogRepositoryMock.Verify(
            x => x.DeleteOlderThanAsync(It.IsAny<DateTimeOffset>()),
            Times.Once);

        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString().Contains("Deleted 0 entries")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task Execute_CalculatesCutoffDateCorrectly()
    {
        // Arrange
        var beforeExecution = DateTimeOffset.UtcNow.AddDays(-Constants.AUDIT_LOG_RETENTION_DAYS);
        DateTimeOffset capturedCutoffDate = DateTimeOffset.MinValue;

        _auditLogRepositoryMock
            .Setup(x => x.DeleteOlderThanAsync(It.IsAny<DateTimeOffset>()))
            .Callback<DateTimeOffset>(cutoff => capturedCutoffDate = cutoff)
            .ReturnsAsync(10);

        // Act
        await _auditLogCleanupJob.Execute(_jobExecutionContextMock.Object);
        var afterExecution = DateTimeOffset.UtcNow.AddDays(-Constants.AUDIT_LOG_RETENTION_DAYS);

        // Assert
        capturedCutoffDate.Should().BeOnOrAfter(beforeExecution);
        capturedCutoffDate.Should().BeOnOrBefore(afterExecution);
        capturedCutoffDate.Offset.Should().Be(TimeSpan.Zero, "cutoff date should use UTC");
    }

    [Fact]
    public async Task Execute_UsesConfiguredRetentionDays()
    {
        // Arrange
        var expectedRetentionDays = Constants.AUDIT_LOG_RETENTION_DAYS;
        DateTimeOffset capturedCutoffDate = DateTimeOffset.MinValue;

        _auditLogRepositoryMock
            .Setup(x => x.DeleteOlderThanAsync(It.IsAny<DateTimeOffset>()))
            .Callback<DateTimeOffset>(cutoff => capturedCutoffDate = cutoff)
            .ReturnsAsync(5);

        // Act
        await _auditLogCleanupJob.Execute(_jobExecutionContextMock.Object);

        // Assert
        var approximateExpectedCutoff = DateTimeOffset.UtcNow.AddDays(-expectedRetentionDays);
        var difference = Math.Abs((capturedCutoffDate - approximateExpectedCutoff).TotalSeconds);
        difference.Should().BeLessThan(2, "cutoff date should be calculated using the retention days constant");

        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString().Contains($"retention: {expectedRetentionDays} days")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task Execute_RepositoryThrowsException_LogsErrorAndRethrows()
    {
        // Arrange
        var expectedException = new InvalidOperationException("Database connection failed");
        _auditLogRepositoryMock
            .Setup(x => x.DeleteOlderThanAsync(It.IsAny<DateTimeOffset>()))
            .ThrowsAsync(expectedException);

        // Act
        var act = async () => await _auditLogCleanupJob.Execute(_jobExecutionContextMock.Object);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Database connection failed");

        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString().Contains("Error during audit log cleanup")),
                expectedException,
                It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task Execute_LogsStartMessage()
    {
        // Arrange
        _auditLogRepositoryMock
            .Setup(x => x.DeleteOlderThanAsync(It.IsAny<DateTimeOffset>()))
            .ReturnsAsync(0);

        // Act
        await _auditLogCleanupJob.Execute(_jobExecutionContextMock.Object);

        // Assert
        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString().Contains("Starting audit log cleanup job")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task Execute_LogsCutoffDateAndRetentionDays()
    {
        // Arrange
        _auditLogRepositoryMock
            .Setup(x => x.DeleteOlderThanAsync(It.IsAny<DateTimeOffset>()))
            .ReturnsAsync(25);

        // Act
        await _auditLogCleanupJob.Execute(_jobExecutionContextMock.Object);

        // Assert
        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => 
                    v.ToString().Contains("Deleting audit logs older than") &&
                    v.ToString().Contains($"retention: {Constants.AUDIT_LOG_RETENTION_DAYS} days")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task Execute_PassesCutoffDateToRepository()
    {
        // Arrange
        DateTimeOffset capturedCutoffDate = DateTimeOffset.MinValue;
        _auditLogRepositoryMock
            .Setup(x => x.DeleteOlderThanAsync(It.IsAny<DateTimeOffset>()))
            .Callback<DateTimeOffset>(cutoff => capturedCutoffDate = cutoff)
            .ReturnsAsync(7);

        // Act
        await _auditLogCleanupJob.Execute(_jobExecutionContextMock.Object);

        // Assert
        capturedCutoffDate.Should().NotBe(DateTimeOffset.MinValue, "repository should be called with a valid cutoff date");
        capturedCutoffDate.Should().BeBefore(DateTimeOffset.UtcNow, "cutoff date should be in the past");
        
        var expectedDaysAgo = DateTimeOffset.UtcNow.AddDays(-Constants.AUDIT_LOG_RETENTION_DAYS);
        var difference = Math.Abs((capturedCutoffDate - expectedDaysAgo).TotalMinutes);
        difference.Should().BeLessThan(1, "cutoff date should match the expected retention calculation");
    }

    [Fact]
    public async Task Execute_MultipleExceptions_AllLogged()
    {
        // Arrange
        var exception1 = new InvalidOperationException("First error");
        var exception2 = new Exception("Second error");
        
        _auditLogRepositoryMock
            .SetupSequence(x => x.DeleteOlderThanAsync(It.IsAny<DateTimeOffset>()))
            .ThrowsAsync(exception1)
            .ThrowsAsync(exception2);

        // Act & Assert - First execution
        var act1 = async () => await _auditLogCleanupJob.Execute(_jobExecutionContextMock.Object);
        await act1.Should().ThrowAsync<InvalidOperationException>();

        // Act & Assert - Second execution
        var act2 = async () => await _auditLogCleanupJob.Execute(_jobExecutionContextMock.Object);
        await act2.Should().ThrowAsync<Exception>();

        // Assert both errors were logged
        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString().Contains("Error during audit log cleanup")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task Execute_LargeDeleteCount_LogsCorrectly()
    {
        // Arrange
        var largeDeleteCount = 1_000_000;
        _auditLogRepositoryMock
            .Setup(x => x.DeleteOlderThanAsync(It.IsAny<DateTimeOffset>()))
            .ReturnsAsync(largeDeleteCount);

        // Act
        await _auditLogCleanupJob.Execute(_jobExecutionContextMock.Object);

        // Assert
        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString().Contains($"Deleted {largeDeleteCount} entries")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.Once);
    }
}
