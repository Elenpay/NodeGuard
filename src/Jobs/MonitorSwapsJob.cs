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
using NodeGuard.Services;
using Quartz;

namespace NodeGuard.Jobs;

public class MonitorSwapsJob : IJob
{
    private readonly ILogger<MonitorSwapsJob> _logger;
    private readonly ISchedulerFactory _schedulerFactory;
    private readonly INodeRepository _nodeRepository;
    private readonly ISwapOutRepository _swapOutRepository;
    private readonly ISwapsService _swapsService;

    public MonitorSwapsJob(ILogger<MonitorSwapsJob> logger, ISchedulerFactory schedulerFactory, INodeRepository nodeRepository, ISwapOutRepository swapOutRepository, ISwapsService swapsService)
    {
        _logger = logger;
        _schedulerFactory = schedulerFactory;
        _nodeRepository = nodeRepository;
        _swapOutRepository = swapOutRepository;
        _swapsService = swapsService;
    }

    private void CleanUp(SwapOut swap, string errorMessage)
    {
        _logger.LogError("Error processing swap {SwapId}: {ErrorMessage}", swap.Id, errorMessage);
        swap.Status = SwapOutStatus.Failed;
        swap.ErrorDetails = errorMessage;
        var (updated, error) = _swapOutRepository.Update(swap);
        if (!updated)
        {
            _logger.LogError("Error updating swap {SwapId} to Failed status: {Error}", swap.Id, error);
        }
    }

    public async Task Execute(IJobExecutionContext context)
    {
        _logger.LogInformation("Starting {JobName}... ", nameof(MonitorSwapsJob));
        try
        {
            var managedNodes = await _nodeRepository.GetAllLoopdConfigured(null);

            var scheduler = await _schedulerFactory.GetScheduler();

            var swaps = await _swapOutRepository.GetAllPending();
            foreach (var swap in swaps)
            {
                var node = managedNodes.FirstOrDefault(n => n.Id == swap.NodeId);
                if (node == null)
                {
                    CleanUp(swap, "Node not found or not managed");
                    continue;
                }

                ArgumentNullException.ThrowIfNull(swap.ProviderId, nameof(swap.ProviderId));

                var response = await _swapsService.GetSwapAsync(node, SwapProvider.Loop, swap.ProviderId);
                if (response == null)
                {
                    CleanUp(swap, "Swap not found in provider");
                    continue;
                }

                if (response.Status != swap.Status)
                {
                    _logger.LogInformation("Swap {SwapId} status changed from {OldStatus} to {NewStatus}", swap.Id, swap.Status, response.Status);
                    swap.Status = response.Status;
                    swap.ServiceFeeSats = response.ServerFee;
                    swap.LightningFeeSats = response.OffchainFee;
                    swap.OnChainFeeSats = response.OnchainFee;
                    var (updated, error) = _swapOutRepository.Update(swap);
                    if (!updated)
                    {
                        _logger.LogError("Error updating swap {SwapId}: {Error}", swap.Id, error);
                        continue;
                    }
                }
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error on {JobName}", nameof(MonitorSwapsJob));
            throw new JobExecutionException(e, false);
        }

        _logger.LogInformation("{JobName} ended", nameof(MonitorSwapsJob));
    }
}
