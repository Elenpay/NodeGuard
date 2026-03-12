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
    public async Task Execute_WhenSwapIsPendingAndOld_StillMonitorsSwap()
    {
        // Arrange
        var loopNode = new Node { Id = 3, Endpoint = "localhost:10011", ChannelAdminMacaroon = "mac", LoopdEndpoint = "localhost:11011", LoopdMacaroon = "loopmac" };
        var oldPendingSwap = new SwapOut
        {
            Id = 303,
            NodeId = loopNode.Id,
            Provider = SwapProvider.Loop,
            ProviderId = "old-loop-swap",
            Status = SwapOutStatus.Pending,
            CreationDatetime = DateTimeOffset.UtcNow.AddDays(-45)
        };

        _nodeRepositoryMock
            .Setup(x => x.GetAllConfiguredByProvider(SwapProvider.Loop, null))
            .ReturnsAsync(new List<Node> { loopNode });

        _nodeRepositoryMock
            .Setup(x => x.GetAllConfiguredByProvider(SwapProvider.FortySwap, null))
            .ReturnsAsync(new List<Node>());

        _swapOutRepositoryMock
            .Setup(x => x.GetAllPending())
            .ReturnsAsync(new List<SwapOut> { oldPendingSwap });

        _swapsServiceMock
            .Setup(x => x.GetSwapAsync(loopNode, SwapProvider.Loop, "old-loop-swap", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SwapResponse
            {
                Id = "old-loop-swap",
                HtlcAddress = "bc1qold",
                Status = SwapOutStatus.Pending,
                ServerFee = 0,
                OffchainFee = 0,
                OnchainFee = 0
            });

        // Act
        await _job.Execute(_jobExecutionContextMock.Object);

        // Assert
        _swapOutRepositoryMock.Verify(x => x.GetAllPending(), Times.Once);
        _swapsServiceMock.Verify(
            x => x.GetSwapAsync(loopNode, SwapProvider.Loop, "old-loop-swap", It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
