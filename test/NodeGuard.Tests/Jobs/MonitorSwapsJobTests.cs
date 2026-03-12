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

using Microsoft.Extensions.Logging;
using Grpc.Core;
using NodeGuard.Data.Models;
using NodeGuard.Data.Repositories.Interfaces;
using NodeGuard.Services;
using Quartz;

namespace NodeGuard.Jobs;

public class MonitorSwapsJobTests
{
    private readonly Mock<ILogger<MonitorSwapsJob>> _loggerMock;
    private readonly Mock<ISchedulerFactory> _schedulerFactoryMock;
    private readonly Mock<INodeRepository> _nodeRepositoryMock;
    private readonly Mock<ISwapOutRepository> _swapOutRepositoryMock;
    private readonly Mock<ISwapsService> _swapsServiceMock;
    private readonly Mock<IAuditService> _auditServiceMock;
    private readonly Mock<IJobExecutionContext> _jobExecutionContextMock;
    private readonly Mock<IScheduler> _schedulerMock;

    private readonly MonitorSwapsJob _job;

    public MonitorSwapsJobTests()
    {
        _loggerMock = new Mock<ILogger<MonitorSwapsJob>>();
        _schedulerFactoryMock = new Mock<ISchedulerFactory>();
        _nodeRepositoryMock = new Mock<INodeRepository>();
        _swapOutRepositoryMock = new Mock<ISwapOutRepository>();
        _swapsServiceMock = new Mock<ISwapsService>();
        _auditServiceMock = new Mock<IAuditService>();
        _jobExecutionContextMock = new Mock<IJobExecutionContext>();
        _schedulerMock = new Mock<IScheduler>();

        _schedulerFactoryMock
            .Setup(x => x.GetScheduler(It.IsAny<CancellationToken>()))
            .ReturnsAsync(_schedulerMock.Object);

        _job = new MonitorSwapsJob(
            _loggerMock.Object,
            _schedulerFactoryMock.Object,
            _nodeRepositoryMock.Object,
            _swapOutRepositoryMock.Object,
            _swapsServiceMock.Object,
            _auditServiceMock.Object);
    }

    [Fact]
    public async Task Execute_WhenProviderCallFails_ContinuesMonitoringOtherSwaps()
    {
        // Arrange
        var loopNode = new Node { Id = 1, Endpoint = "localhost:10009", ChannelAdminMacaroon = "mac", LoopdEndpoint = "localhost:11010", LoopdMacaroon = "loopmac" };
        var fortyNode = new Node { Id = 2, Endpoint = "localhost:10010", ChannelAdminMacaroon = "mac", FortySwapEndpoint = "localhost:50051" };

        var failingSwap = new SwapOut
        {
            Id = 101,
            NodeId = loopNode.Id,
            Provider = SwapProvider.Loop,
            ProviderId = "loop-swap-1",
            Status = SwapOutStatus.Pending
        };

        var succeedingSwap = new SwapOut
        {
            Id = 202,
            NodeId = fortyNode.Id,
            Provider = SwapProvider.FortySwap,
            ProviderId = "40swap-2",
            Status = SwapOutStatus.Pending
        };

        _nodeRepositoryMock
            .Setup(x => x.GetAllConfiguredByProvider(SwapProvider.Loop, null))
            .ReturnsAsync(new List<Node> { loopNode });

        _nodeRepositoryMock
            .Setup(x => x.GetAllConfiguredByProvider(SwapProvider.FortySwap, null))
            .ReturnsAsync(new List<Node> { fortyNode });

        _swapOutRepositoryMock
            .Setup(x => x.GetAllPending())
            .ReturnsAsync(new List<SwapOut> { failingSwap, succeedingSwap });

        _swapsServiceMock
            .Setup(x => x.GetSwapAsync(loopNode, SwapProvider.Loop, "loop-swap-1", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("loop unavailable"));

        _swapsServiceMock
            .Setup(x => x.GetSwapAsync(fortyNode, SwapProvider.FortySwap, "40swap-2", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SwapResponse
            {
                Id = "40swap-2",
                HtlcAddress = "bc1qtest",
                Status = SwapOutStatus.Completed,
                ServerFee = 11,
                OffchainFee = 22,
                OnchainFee = 33
            });

        _swapOutRepositoryMock
            .Setup(x => x.Update(It.IsAny<SwapOut>()))
            .Returns((true, null));

        // Act
        await _job.Execute(_jobExecutionContextMock.Object);

        // Assert
        _swapsServiceMock.Verify(
            x => x.GetSwapAsync(loopNode, SwapProvider.Loop, "loop-swap-1", It.IsAny<CancellationToken>()),
            Times.Once);

        _swapsServiceMock.Verify(
            x => x.GetSwapAsync(fortyNode, SwapProvider.FortySwap, "40swap-2", It.IsAny<CancellationToken>()),
            Times.Once);

        _swapOutRepositoryMock.Verify(
            x => x.Update(It.Is<SwapOut>(s => s.Id == succeedingSwap.Id && s.Status == SwapOutStatus.Completed)),
            Times.Once);

        _swapOutRepositoryMock.Verify(
            x => x.Update(It.Is<SwapOut>(s => s.Id == failingSwap.Id)),
            Times.Never);
    }

    [Fact]
    public async Task Execute_WhenProviderTimesOutWithUnavailableRpc_ContinuesWithOtherSwaps()
    {
        // Arrange
        var fortyNode = new Node { Id = 10, Endpoint = "localhost:10010", ChannelAdminMacaroon = "mac", FortySwapEndpoint = "localhost:50051" };
        var loopNode = new Node { Id = 11, Endpoint = "localhost:10011", ChannelAdminMacaroon = "mac", LoopdEndpoint = "localhost:11011", LoopdMacaroon = "loopmac" };

        var timeoutSwap = new SwapOut
        {
            Id = 401,
            NodeId = fortyNode.Id,
            Provider = SwapProvider.FortySwap,
            ProviderId = "40swap-timeout",
            Status = SwapOutStatus.Pending
        };

        var healthySwap = new SwapOut
        {
            Id = 402,
            NodeId = loopNode.Id,
            Provider = SwapProvider.Loop,
            ProviderId = "loop-ok",
            Status = SwapOutStatus.Pending
        };

        _nodeRepositoryMock
            .Setup(x => x.GetAllConfiguredByProvider(SwapProvider.Loop, null))
            .ReturnsAsync(new List<Node> { loopNode });

        _nodeRepositoryMock
            .Setup(x => x.GetAllConfiguredByProvider(SwapProvider.FortySwap, null))
            .ReturnsAsync(new List<Node> { fortyNode });

        _swapOutRepositoryMock
            .Setup(x => x.GetAllPending())
            .ReturnsAsync(new List<SwapOut> { timeoutSwap, healthySwap });

        var timeoutException = new RpcException(new Status(
            StatusCode.Unavailable,
            "Error starting gRPC call. HttpRequestException: Connection timed out (example:50051) SocketException: Connection timed out"));

        _swapsServiceMock
            .Setup(x => x.GetSwapAsync(fortyNode, SwapProvider.FortySwap, "40swap-timeout", It.IsAny<CancellationToken>()))
            .ThrowsAsync(timeoutException);

        _swapsServiceMock
            .Setup(x => x.GetSwapAsync(loopNode, SwapProvider.Loop, "loop-ok", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SwapResponse
            {
                Id = "loop-ok",
                HtlcAddress = "bc1qok",
                Status = SwapOutStatus.Completed,
                ServerFee = 7,
                OffchainFee = 3,
                OnchainFee = 9
            });

        _swapOutRepositoryMock
            .Setup(x => x.Update(It.IsAny<SwapOut>()))
            .Returns((true, null));

        // Act
        await _job.Execute(_jobExecutionContextMock.Object);

        // Assert
        _swapsServiceMock.Verify(
            x => x.GetSwapAsync(fortyNode, SwapProvider.FortySwap, "40swap-timeout", It.IsAny<CancellationToken>()),
            Times.Once);

        _swapsServiceMock.Verify(
            x => x.GetSwapAsync(loopNode, SwapProvider.Loop, "loop-ok", It.IsAny<CancellationToken>()),
            Times.Once);

        _swapOutRepositoryMock.Verify(
            x => x.Update(It.Is<SwapOut>(s => s.Id == healthySwap.Id && s.Status == SwapOutStatus.Completed)),
            Times.Once);

        _swapOutRepositoryMock.Verify(
            x => x.Update(It.Is<SwapOut>(s => s.Id == timeoutSwap.Id)),
            Times.Never);
    }

    [Fact]
    public async Task Execute_WhenProviderMarksSwapCompleted_UpdatesSwapAndAuditsSuccess()
    {
        // Arrange
        var loopNode = new Node { Id = 21, Endpoint = "localhost:10021", ChannelAdminMacaroon = "mac", LoopdEndpoint = "localhost:11021", LoopdMacaroon = "loopmac" };
        var pendingSwap = new SwapOut
        {
            Id = 501,
            NodeId = loopNode.Id,
            Provider = SwapProvider.Loop,
            ProviderId = "loop-success-501",
            Status = SwapOutStatus.Pending,
            SatsAmount = 250_000,
            IsManual = false
        };

        _nodeRepositoryMock
            .Setup(x => x.GetAllConfiguredByProvider(SwapProvider.Loop, null))
            .ReturnsAsync(new List<Node> { loopNode });

        _nodeRepositoryMock
            .Setup(x => x.GetAllConfiguredByProvider(SwapProvider.FortySwap, null))
            .ReturnsAsync(new List<Node>());

        _swapOutRepositoryMock
            .Setup(x => x.GetAllPending())
            .ReturnsAsync(new List<SwapOut> { pendingSwap });

        _swapsServiceMock
            .Setup(x => x.GetSwapAsync(loopNode, SwapProvider.Loop, "loop-success-501", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SwapResponse
            {
                Id = "loop-success-501",
                HtlcAddress = "bc1qsuccess",
                Status = SwapOutStatus.Completed,
                ServerFee = 10,
                OffchainFee = 20,
                OnchainFee = 30
            });

        _swapOutRepositoryMock
            .Setup(x => x.Update(It.IsAny<SwapOut>()))
            .Returns((true, null));

        // Act
        await _job.Execute(_jobExecutionContextMock.Object);

        // Assert
        _swapOutRepositoryMock.Verify(
            x => x.Update(It.Is<SwapOut>(s =>
                s.Id == pendingSwap.Id &&
                s.Status == SwapOutStatus.Completed &&
                s.ServiceFeeSats == 10 &&
                s.LightningFeeSats == 20 &&
                s.OnChainFeeSats == 30)),
            Times.Once);

        _auditServiceMock.Verify(
            x => x.LogSystemAsync(
                AuditActionType.SwapOutCompleted,
                AuditEventType.Success,
                AuditObjectType.SwapOut,
                pendingSwap.ProviderId,
                It.IsAny<object?>()),
            Times.Once);
    }

    [Fact]
    public async Task Execute_WhenProviderMarksSwapFailed_UpdatesSwapAndAuditsFailure()
    {
        // Arrange
        var fortyNode = new Node { Id = 22, Endpoint = "localhost:10022", ChannelAdminMacaroon = "mac", FortySwapEndpoint = "localhost:50052" };
        var pendingSwap = new SwapOut
        {
            Id = 502,
            NodeId = fortyNode.Id,
            Provider = SwapProvider.FortySwap,
            ProviderId = "40swap-failed-502",
            Status = SwapOutStatus.Pending,
            SatsAmount = 300_000,
            IsManual = true
        };

        _nodeRepositoryMock
            .Setup(x => x.GetAllConfiguredByProvider(SwapProvider.Loop, null))
            .ReturnsAsync(new List<Node>());

        _nodeRepositoryMock
            .Setup(x => x.GetAllConfiguredByProvider(SwapProvider.FortySwap, null))
            .ReturnsAsync(new List<Node> { fortyNode });

        _swapOutRepositoryMock
            .Setup(x => x.GetAllPending())
            .ReturnsAsync(new List<SwapOut> { pendingSwap });

        _swapsServiceMock
            .Setup(x => x.GetSwapAsync(fortyNode, SwapProvider.FortySwap, "40swap-failed-502", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SwapResponse
            {
                Id = "40swap-failed-502",
                HtlcAddress = string.Empty,
                Status = SwapOutStatus.Failed,
                ServerFee = 8,
                OffchainFee = 6,
                OnchainFee = 4,
                ErrorMessage = "contract expired"
            });

        _swapOutRepositoryMock
            .Setup(x => x.Update(It.IsAny<SwapOut>()))
            .Returns((true, null));

        // Act
        await _job.Execute(_jobExecutionContextMock.Object);

        // Assert
        _swapOutRepositoryMock.Verify(
            x => x.Update(It.Is<SwapOut>(s =>
                s.Id == pendingSwap.Id &&
                s.Status == SwapOutStatus.Failed &&
                s.ErrorDetails == "contract expired" &&
                s.ServiceFeeSats == 8 &&
                s.LightningFeeSats == 6 &&
                s.OnChainFeeSats == 4)),
            Times.Once);

        _auditServiceMock.Verify(
            x => x.LogSystemAsync(
                AuditActionType.SwapOutCompleted,
                AuditEventType.Failure,
                AuditObjectType.SwapOut,
                pendingSwap.ProviderId,
                It.IsAny<object?>()),
            Times.Once);
    }
}
