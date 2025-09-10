using NodeGuard.Data.Models;
using NodeGuard.Data.Repositories.Interfaces;
using NodeGuard.Helpers;
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
        _nodeRepository = nodeRepository;
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
            var managedNodes = await _nodeRepository.GetAllLoopConfigured(null);

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
