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
using NodeGuard.Data.Models;
using NodeGuard.Services;
using Quartz;

namespace NodeGuard.Jobs;

public class RetryRebalanceJobTests
{
    private readonly Mock<ILogger<RebalanceJob>> _logger = new();
    private readonly Mock<IRebalanceService> _rebalanceService = new();

    private RebalanceJob CreateJob() => new(_logger.Object, _rebalanceService.Object);

    private static IJobExecutionContext BuildContext(int rebalanceId, CancellationToken token = default)
    {
        var ctxMock = new Mock<IJobExecutionContext>();
        var data = new JobDataMap();
        data.Put("rebalanceId", rebalanceId);
        ctxMock.Setup(c => c.MergedJobDataMap).Returns(data);
        ctxMock.Setup(c => c.CancellationToken).Returns(token);
        return ctxMock.Object;
    }

    [Fact]
    public async Task Execute_DelegatesToServiceWithRebalanceIdFromJobData()
    {
        _rebalanceService.Setup(s => s.ExecuteAsync(123, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Rebalance { Id = 123, Status = RebalanceStatus.Succeeded });

        await CreateJob().Execute(BuildContext(123));

        _rebalanceService.Verify(s => s.ExecuteAsync(123, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Execute_PropagatesCancellationToken()
    {
        using var cts = new CancellationTokenSource();
        _rebalanceService.Setup(s => s.ExecuteAsync(7, cts.Token))
            .ReturnsAsync(new Rebalance { Id = 7 });

        await CreateJob().Execute(BuildContext(7, cts.Token));

        _rebalanceService.Verify(s => s.ExecuteAsync(7, cts.Token), Times.Once);
    }

    [Fact]
    public async Task Execute_ServiceThrows_RethrowsAsJobExecutionException()
    {
        _rebalanceService.Setup(s => s.ExecuteAsync(99, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var act = () => CreateJob().Execute(BuildContext(99));

        await act.Should().ThrowAsync<JobExecutionException>();
    }
}
