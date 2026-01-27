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
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;
using NodeGuard.Data.Models;
using NodeGuard.Data.Repositories.Interfaces;
using NodeGuard.Helpers;
using NodeGuard.Services;
using Xunit;

namespace NodeGuard.Tests.Services;

public class AuditServiceTests
{
    private readonly Mock<IAuditLogRepository> _auditLogRepositoryMock;
    private readonly Mock<IHttpContextAccessor> _httpContextAccessorMock;
    private readonly Mock<ILogger<AuditService>> _loggerMock;

    public AuditServiceTests()
    {
        _auditLogRepositoryMock = new Mock<IAuditLogRepository>();
        _httpContextAccessorMock = new Mock<IHttpContextAccessor>();
        _loggerMock = new Mock<ILogger<AuditService>>();
    }

    private AuditService CreateAuditService()
    {
        return new AuditService(
            _auditLogRepositoryMock.Object,
            _httpContextAccessorMock.Object,
            _loggerMock.Object);
    }

    private void SetupSuccessfulRepository()
    {
        _auditLogRepositoryMock
            .Setup(x => x.AddAsync(It.IsAny<AuditLog>()))
            .ReturnsAsync((true, (string?)null));
    }

    private void SetupFailedRepository(string errorMessage = "Database error")
    {
        _auditLogRepositoryMock
            .Setup(x => x.AddAsync(It.IsAny<AuditLog>()))
            .ReturnsAsync((false, errorMessage));
    }

    private DefaultHttpContext CreateHttpContextWithUser(
        string userId = "user-123",
        string? userName = "testuser",
        string? email = null,
        string? identityName = null,
        string ipAddress = "192.168.1.100")
    {
        var httpContext = new DefaultHttpContext();
        
        var claims = new List<Claim>();
        if (userId != null)
            claims.Add(new Claim(ClaimTypes.NameIdentifier, userId));
        if (userName != null)
            claims.Add(new Claim(ClaimTypes.Name, userName));
        if (email != null)
            claims.Add(new Claim(ClaimTypes.Email, email));

        var identity = identityName != null 
            ? new ClaimsIdentity(claims, "TestAuth", identityName, null)
            : new ClaimsIdentity(claims, "TestAuth");
        
        httpContext.User = new ClaimsPrincipal(identity);
        httpContext.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(ipAddress);

        return httpContext;
    }

    #region LogAsync_AutoContext

    [Fact]
    public async Task LogAsync_AutoContext_WithAuthenticatedUser_ExtractsUserInfoFromClaims()
    {
        // Arrange
        SetupSuccessfulRepository();
        var httpContext = CreateHttpContextWithUser("user-123", "johndoe");
        _httpContextAccessorMock.Setup(x => x.HttpContext).Returns(httpContext);
        var service = CreateAuditService();

        // Act
        await service.LogAsync(
            AuditActionType.Create,
            AuditEventType.Success,
            AuditObjectType.Wallet,
            "wallet-456",
            new { Amount = 50000 });

        // Assert
        _auditLogRepositoryMock.Verify(
            x => x.AddAsync(It.Is<AuditLog>(log =>
                log.UserId == "user-123" &&
                log.Username == "johndoe" &&
                log.IpAddress == "192.168.1.100")),
            Times.Once);
    }

    [Fact]
    public async Task LogAsync_AutoContext_NoHttpContext_HandlesGracefully()
    {
        // Arrange
        SetupSuccessfulRepository();
        _httpContextAccessorMock.Setup(x => x.HttpContext).Returns((HttpContext?)null);
        var service = CreateAuditService();

        // Act
        await service.LogAsync(
            AuditActionType.Create,
            AuditEventType.Success,
            AuditObjectType.Wallet,
            "wallet-456",
            new { Amount = 50000 });

        // Assert
        _auditLogRepositoryMock.Verify(
            x => x.AddAsync(It.Is<AuditLog>(log =>
                log.UserId == null &&
                log.Username == null &&
                log.IpAddress == null)),
            Times.Once);
    }

    [Fact]
    public async Task LogAsync_AutoContext_UnauthenticatedUser_HandlesGracefully()
    {
        // Arrange
        SetupSuccessfulRepository();
        var httpContext = new DefaultHttpContext();
        httpContext.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("192.168.1.200");
        _httpContextAccessorMock.Setup(x => x.HttpContext).Returns(httpContext);
        var service = CreateAuditService();

        // Act
        await service.LogAsync(
            AuditActionType.Update,
            AuditEventType.Success,
            AuditObjectType.Channel,
            "channel-789",
            null);

        // Assert
        _auditLogRepositoryMock.Verify(
            x => x.AddAsync(It.Is<AuditLog>(log =>
                log.UserId == null &&
                log.Username == null &&
                log.IpAddress == "192.168.1.200")),
            Times.Once);
    }

    [Fact]
    public async Task LogAsync_AutoContext_RepositoryFails_LogsErrorButDoesNotThrow()
    {
        // Arrange
        SetupFailedRepository("Database connection failed");
        var httpContext = CreateHttpContextWithUser();
        _httpContextAccessorMock.Setup(x => x.HttpContext).Returns(httpContext);
        var service = CreateAuditService();

        // Act
        var act = async () => await service.LogAsync(
            AuditActionType.Create,
            AuditEventType.Failure,
            AuditObjectType.User,
            "user-123",
            null);

        // Assert
        await act.Should().NotThrowAsync();

        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Failed to persist audit log")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task LogAsync_AutoContext_ExceptionDuringLogging_CatchesAndLogsError()
    {
        // Arrange
        var httpContext = CreateHttpContextWithUser();
        _httpContextAccessorMock.Setup(x => x.HttpContext).Returns(httpContext);
        var service = CreateAuditService();
        _auditLogRepositoryMock
            .Setup(x => x.AddAsync(It.IsAny<AuditLog>()))
            .ThrowsAsync(new InvalidOperationException("Database error"));

        // Act
        var act = async () => await service.LogAsync(
            AuditActionType.Create,
            AuditEventType.Success,
            AuditObjectType.User,
            "user-123",
            new { Test = "data" });

        // Assert
        await act.Should().NotThrowAsync();

        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Error logging audit event")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }
    
    #endregion

    #region LogSystemAsync

    [Fact]
    public async Task LogSystemAsync_SuccessfulLogging_CreatesAuditLogWithSystemUser()
    {
        // Arrange
        SetupSuccessfulRepository();
        var service = CreateAuditService();

        // Act
        await service.LogSystemAsync(
            AuditActionType.Create,
            AuditEventType.Success,
            AuditObjectType.Wallet,
            "wallet-123",
            new { AutoGenerated = true });

        // Assert
        _auditLogRepositoryMock.Verify(
            x => x.AddAsync(It.Is<AuditLog>(log =>
                log.ActionType == AuditActionType.Create &&
                log.EventType == AuditEventType.Success &&
                log.ObjectAffected == AuditObjectType.Wallet &&
                log.ObjectId == "wallet-123" &&
                log.UserId == null &&
                log.Username == "SYSTEM" &&
                log.IpAddress == null &&
                log.Details != null)),
            Times.Once);

        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("AUDIT:") && v.ToString()!.Contains("SYSTEM")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task LogSystemAsync_WithoutDetails_LogsSuccessfully()
    {
        // Arrange
        SetupSuccessfulRepository();
        var service = CreateAuditService();

        // Act
        await service.LogSystemAsync(
            AuditActionType.Update,
            AuditEventType.Success,
            AuditObjectType.Channel,
            "channel-456",
            null);

        // Assert
        _auditLogRepositoryMock.Verify(
            x => x.AddAsync(It.Is<AuditLog>(log =>
                log.Username == "SYSTEM" &&
                log.Details == null)),
            Times.Once);
    }

    [Fact]
    public async Task LogSystemAsync_RepositoryFails_LogsErrorButDoesNotThrow()
    {
        // Arrange
        SetupFailedRepository("Database timeout");
        var service = CreateAuditService();

        // Act
        var act = async () => await service.LogSystemAsync(
            AuditActionType.Create,
            AuditEventType.Success,
            AuditObjectType.Wallet,
            "wallet-123");

        // Assert
        await act.Should().NotThrowAsync();

        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Failed to persist audit log")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task LogSystemAsync_ExceptionDuringLogging_CatchesAndLogsError()
    {
        // Arrange
        var service = CreateAuditService();
        _auditLogRepositoryMock
            .Setup(x => x.AddAsync(It.IsAny<AuditLog>()))
            .ThrowsAsync(new InvalidOperationException("Database error"));

        // Act
        var act = async () => await service.LogSystemAsync(
            AuditActionType.Delete,
            AuditEventType.Success,
            AuditObjectType.Node,
            "node-789",
            new { Reason = "Automated cleanup" });

        // Assert
        await act.Should().NotThrowAsync();

        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Error logging audit event")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    #endregion
}
