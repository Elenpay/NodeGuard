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
    private readonly IAuditService _auditService;

    public MonitorSwapsJob(ILogger<MonitorSwapsJob> logger, ISchedulerFactory schedulerFactory, INodeRepository nodeRepository, ISwapOutRepository swapOutRepository, ISwapsService swapsService, IAuditService auditService)
    {
        _logger = logger;
        _schedulerFactory = schedulerFactory;
        _nodeRepository = nodeRepository;
        _swapOutRepository = swapOutRepository;
        _swapsService = swapsService;
        _auditService = auditService;
    }

    private async Task LogMonitoringIssueAsync(SwapOut swap, string errorMessage)
    {
        await _auditService.LogSystemAsync(
            AuditActionType.Update,
            AuditEventType.Attempt,
            AuditObjectType.SwapOut,
            swap.ProviderId ?? swap.Id.ToString(),
            new
            {
                SwapId = swap.Id,
                NodeId = swap.NodeId,
                Provider = swap.Provider.ToString(),
                ProviderId = swap.ProviderId,
                IsManual = swap.IsManual,
                CurrentStatus = swap.Status.ToString(),
                ErrorMessage = errorMessage
            });
    }

    public async Task Execute(IJobExecutionContext context)
    {
        _logger.LogInformation("Starting {JobName}... ", nameof(MonitorSwapsJob));
        try
        {
            var loopNodes = await _nodeRepository.GetAllConfiguredByProvider(SwapProvider.Loop, null);
            var fortySwapNodes = await _nodeRepository.GetAllConfiguredByProvider(SwapProvider.FortySwap, null);
            var managedNodes = loopNodes.Concat(fortySwapNodes).Distinct().ToList();

            var swaps = await _swapOutRepository.GetAllPending();
            foreach (var swap in swaps)
            {
                try
                {
                    var node = managedNodes.FirstOrDefault(n => n.Id == swap.NodeId);
                    if (node == null)
                    {
                        await LogMonitoringIssueAsync(swap, "Node not found or not managed");
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(swap.ProviderId))
                    {
                        await LogMonitoringIssueAsync(swap, "Swap provider id is missing");
                        continue;
                    }

                    SwapResponse? response;
                    try
                    {
                        response = await _swapsService.GetSwapAsync(node, swap.Provider, swap.ProviderId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex,
                            "Error querying provider {Provider} for swap {SwapId}.",
                            swap.Provider,
                            swap.Id);
                        continue;
                    }

                    if (response == null)
                    {
                        await LogMonitoringIssueAsync(swap, "Swap not found in provider");
                        continue;
                    }

                    if (response.Status != swap.Status)
                    {
                        var oldStatus = swap.Status;

                        if (response.Status == SwapOutStatus.Failed)
                        {
                            _logger.LogWarning("Swap {SwapId} status changed from {OldStatus} to {NewStatus}. Error: {ErrorMessage}",
                                swap.Id, swap.Status, response.Status, response.ErrorMessage);
                            swap.ErrorDetails = response.ErrorMessage;
                        }
                        else
                        {
                            _logger.LogInformation("Swap {SwapId} status changed from {OldStatus} to {NewStatus}", swap.Id, swap.Status, response.Status);
                        }

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

                        // Audit swap completion (only for successful completions)
                        if (response.Status == SwapOutStatus.Completed)
                        {
                            await _auditService.LogSystemAsync(
                                AuditActionType.SwapOutCompleted,
                                AuditEventType.Success,
                                AuditObjectType.SwapOut,
                                swap.ProviderId,
                                new
                                {
                                    SwapId = swap.Id,
                                    NodeId = swap.NodeId,
                                    Provider = swap.Provider.ToString(),
                                    ProviderId = swap.ProviderId,
                                    AmountSats = swap.SatsAmount,
                                    TotalFeeSats = swap.TotalFees.Satoshi,
                                    ServiceFeeSats = swap.ServiceFeeSats,
                                    LightningFeeSats = swap.LightningFeeSats,
                                    OnChainFeeSats = swap.OnChainFeeSats,
                                    IsManual = swap.IsManual,
                                    OldStatus = oldStatus.ToString(),
                                    NewStatus = response.Status.ToString()
                                });
                        }
                        else if (response.Status == SwapOutStatus.Failed)
                        {
                            await _auditService.LogSystemAsync(
                                AuditActionType.SwapOutCompleted,
                                AuditEventType.Failure,
                                AuditObjectType.SwapOut,
                                swap.ProviderId,
                                new
                                {
                                    SwapId = swap.Id,
                                    NodeId = swap.NodeId,
                                    Provider = swap.Provider.ToString(),
                                    ProviderId = swap.ProviderId,
                                    AmountSats = swap.SatsAmount,
                                    TotalFeeSats = swap.TotalFees.Satoshi,
                                    ServiceFeeSats = swap.ServiceFeeSats,
                                    LightningFeeSats = swap.LightningFeeSats,
                                    OnChainFeeSats = swap.OnChainFeeSats,
                                    IsManual = swap.IsManual,
                                    OldStatus = oldStatus.ToString(),
                                    NewStatus = response.Status.ToString(),
                                    ErrorMessage = response.ErrorMessage
                                });
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Unexpected error while processing swap {SwapId} for provider {Provider}. Monitoring will continue for other swaps",
                        swap.Id,
                        swap.Provider);
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
