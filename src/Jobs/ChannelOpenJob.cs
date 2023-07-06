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

using FundsManager.Data.Repositories.Interfaces;
using FundsManager.Services;
using FundsManager.Helpers;
using FundsManager.Data.Models;
using Quartz;
using Quartz.Impl.Triggers;

namespace FundsManager.Jobs;

/// <summary>
/// Job for openning channel requests to the managed nodes, with automatic retry
/// </summary>
/// <returns></returns>
[DisallowConcurrentExecution]
public class ChannelOpenJob : IJob
{
    private readonly ILogger<ChannelOpenJob> _logger;
    private readonly ILightningService _lightningService;
    private readonly IChannelOperationRequestRepository _channelOperationRequestRepository;


    public ChannelOpenJob(ILogger<ChannelOpenJob> logger, ILightningService lightningService, IChannelOperationRequestRepository channelOperationRequestRepository)
    {
        _logger = logger;
        _lightningService = lightningService;
        _channelOperationRequestRepository = channelOperationRequestRepository;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        _logger.LogInformation("Starting {JobName}... ", nameof(ChannelOpenJob));
        var data = context.JobDetail.JobDataMap;
        var openRequestId = data.GetInt("openRequestId");
        try
        {
            await RetriableJob.Execute(context, async () =>
            {
                var openRequest = await _channelOperationRequestRepository.GetById(openRequestId);
                await _lightningService.OpenChannel(openRequest);
            });
        }
        catch (Exception e)
        {
            var shouldRetry = await LogToChannelRequest(openRequestId, e, context);

            await RetriableJob.OnFail(context, async () =>
            {
                var request = await _channelOperationRequestRepository.GetById(openRequestId);
                request.Status = ChannelOperationRequestStatus.Failed;
                _channelOperationRequestRepository.Update(request);
            });

            _logger.LogError(e, "Error on {JobName}", nameof(ChannelOpenJob));
            if (!shouldRetry)
            {
                await context.Scheduler.DeleteJob(context.JobDetail.Key, context.CancellationToken);
            }
            throw new JobExecutionException(e, false);
        }

        _logger.LogInformation("{JobName} ended", nameof(ChannelOpenJob));
    }

    private async Task<bool> LogToChannelRequest(int openRequestId, Exception e, IJobExecutionContext context)
    {
        try
        {
            var request = await _channelOperationRequestRepository.GetById(openRequestId);
            if (e is PeerNotOnlineException or RemoteCanceledFundingException)
            {
                request.StatusLogs.Add(ChannelStatusLog.Error(e.Message));
            }
            else
            {
                request.StatusLogs.Add(ChannelStatusLog.Error("Unexpected exception trying to open channel"));
            }

            if (e is RemoteCanceledFundingException)
            {
                request.Status = ChannelOperationRequestStatus.Failed;
                _channelOperationRequestRepository.Update(request);
                return false;
            }

            var nextRetry = RetriableJob.GetNextInterval(context);
            if (nextRetry != null)
            {
                request.StatusLogs.Add(ChannelStatusLog.Info($"Next retry in {nextRetry} minutes"));
            }
            _channelOperationRequestRepository.Update(request);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error logging to channel request");
        }

        return true;
    }
}