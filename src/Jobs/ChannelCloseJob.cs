// NodeGuard
// Copyright (C) 2025  Elenpay
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY, without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Affero General Public License for more details.
//
// You should have received a copy of the GNU Affero General Public License
// along with this program.  If not, see http://www.gnu.org/licenses/.



using NodeGuard.Data.Models;
using NodeGuard.Data.Repositories.Interfaces;
using NodeGuard.Helpers;
using NodeGuard.Services;
using Quartz;

namespace NodeGuard.Jobs;

/// <summary>
/// Job for closing channel requests to the managed nodes, with automatic retry
/// </summary>
/// <returns></returns>
[DisallowConcurrentExecution]
public class ChannelCloseJob : IJob
{
    private readonly ILogger<ChannelCloseJob> _logger;
    private readonly ILightningService _lightningService;
    private readonly IChannelOperationRequestRepository _channelOperationRequestRepository;


    public ChannelCloseJob(ILogger<ChannelCloseJob> logger, ILightningService lightningService, IChannelOperationRequestRepository channelOperationRequestRepository)
    {
        _logger = logger;
        _lightningService = lightningService;
        _channelOperationRequestRepository = channelOperationRequestRepository;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        _logger.LogInformation("Starting {JobName}... ", nameof(ChannelCloseJob));
        var data = context.JobDetail.JobDataMap;
        var closeRequestId = data.GetInt("closeRequestId");
        try
        {
            await RetriableJob.Execute(context, async () =>
            {
                var forceClose = data.GetBoolean("forceClose");
                var closeRequest = await _channelOperationRequestRepository.GetById(closeRequestId);
                await _lightningService.CloseChannel(closeRequest, forceClose);
            });
        }
        catch (Exception e)
        {
            await RetriableJob.OnFail(context, async () =>
            {
                var request = await _channelOperationRequestRepository.GetById(closeRequestId);
                request.Status = ChannelOperationRequestStatus.Failed;
                _channelOperationRequestRepository.Update(request);
            });

            _logger.LogError(e, "Error on {JobName}", nameof(ChannelCloseJob));
            throw new JobExecutionException(e, false);
        }

        _logger.LogInformation("{JobName} ended", nameof(ChannelCloseJob));
    }
}
