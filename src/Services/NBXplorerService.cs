using FundsManager.Helpers;
using NBitcoin;
using NBXplorer.DerivationStrategy;
using NBXplorer.Models;

namespace FundsManager.Services;

public interface INBXplorerService
{
    public Task TrackAsync(DerivationStrategyBase derivationStrategyBase, CancellationToken cancellation = default);

    public Task TrackAsync(TrackedSource trackedSource, CancellationToken cancellation = default);

    public Task<TransactionResult?> GetTransactionAsync(uint256 txId, CancellationToken cancellation = default);

    public Task<KeyPathInformation?> GetUnusedAsync(DerivationStrategyBase strategy, DerivationFeature feature,
        int skip = 0, bool reserve = false, CancellationToken cancellation = default);

    public Task<GetBalanceResponse> GetBalanceAsync(DerivationStrategyBase userDerivationScheme,
        CancellationToken cancellation = default);

    public Task<StatusResult> GetStatusAsync(CancellationToken cancellation = default);

    public Task<UTXOChanges> GetUTXOsAsync(DerivationStrategyBase extKey, CancellationToken cancellation = default);

    public Task<GetFeeRateResult> GetFeeRateAsync(int blockCount, FeeRate fallbackFeeRate,
        CancellationToken cancellation = default);

    public Task<MempoolRecommendedFees> GetMempoolRecommendedFeesAsync(CancellationToken cancellation = default);


    public Task<BroadcastResult> BroadcastAsync(Transaction tx, bool testMempoolAccept,
        CancellationToken cancellation = default);

    public Task ScanUTXOSetAsync(DerivationStrategyBase extKey,
        int? batchSize = null,
        int? gapLimit = null,
        int? fromIndex = null,
        CancellationToken cancellation = default(CancellationToken));

    public Task<ScanUTXOInformation> GetScanUTXOSetInformationAsync(DerivationStrategyBase extKey,
        CancellationToken cancellation = default(CancellationToken));
}

public enum MempoolRecommendedFeesTypes
{
    EconomyFee,
    FastestFee,
    HourFee,
    HalfHourFee,
    CustomFee
}

/// <summary>
/// Response from
/// </summary>
public class MempoolRecommendedFees
{
    public int FastestFee { get; set; }
    public int HalfHourFee { get; set; }
    public int HourFee { get; set; }
    public int EconomyFee { get; set; }
    public int MinimumFee { get; set; }
}

/// <summary>
/// Wrapper for the NBXplorer client to support DI
/// </summary>
public class NBXplorerService : INBXplorerService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<NBXplorerService> _logger;

    public NBXplorerService(HttpClient httpClient, ILogger<NBXplorerService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task TrackAsync(DerivationStrategyBase derivationStrategyBase,
        CancellationToken cancellation = default)
    {
        var client = await LightningHelper.CreateNBExplorerClient();
        await client.TrackAsync(derivationStrategyBase, cancellation: cancellation);
    }

    public async Task TrackAsync(TrackedSource trackedSource, CancellationToken cancellation = default)
    {
        var client = await LightningHelper.CreateNBExplorerClient();

        await client.TrackAsync(trackedSource, cancellation);
    }

    public async Task<TransactionResult?> GetTransactionAsync(uint256 txId, CancellationToken cancellation = default)
    {
        var client = await LightningHelper.CreateNBExplorerClient();

        return await client.GetTransactionAsync(txId, cancellation);
    }

    public async Task<KeyPathInformation?> GetUnusedAsync(DerivationStrategyBase strategy,
        DerivationFeature feature,
        int skip = 0,
        bool reserve = false,
        CancellationToken cancellation = default
    )
    {
        var client = await LightningHelper.CreateNBExplorerClient();

        var keyPathInformation = await client.GetUnusedAsync(strategy, feature, skip, reserve, cancellation);


        return keyPathInformation;
    }

    public async Task<GetBalanceResponse> GetBalanceAsync(DerivationStrategyBase userDerivationScheme,
        CancellationToken cancellation = default)
    {
        var client = await LightningHelper.CreateNBExplorerClient();

        return await client.GetBalanceAsync(userDerivationScheme, cancellation);
    }


    public async Task<UTXOChanges> GetUTXOsAsync(DerivationStrategyBase extKey,
        CancellationToken cancellation = default)
    {
        var client = await LightningHelper.CreateNBExplorerClient();

        return await client.GetUTXOsAsync(extKey, cancellation);
    }

    public async Task<GetFeeRateResult> GetFeeRateAsync(int blockCount, FeeRate fallbackFeeRate,
            CancellationToken cancellation = default)
    {
        var nbExplorerClient = await LightningHelper.CreateNBExplorerClient();

        //Patch to use mempool.space
        var mempoolEndpoint = Constants.MEMPOOL_ENDPOINT;

        if (string.IsNullOrWhiteSpace(mempoolEndpoint))
            throw new Exception("MEMPOOL_ENDPOINT is not set");

        try
        {

            var recommendedFees =
                await _httpClient.GetFromJsonAsync<MempoolRecommendedFees>($"{mempoolEndpoint}/api/v1/fees/recommended");
            if (recommendedFees != null)
            {
                var feerate = new GetFeeRateResult
                {
                    FeeRate = new FeeRate((decimal) recommendedFees.FastestFee),
                    BlockCount = 1 // 60 mins / 10 mins
                };

                return feerate;
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error getting mempool fees");
        }

        return await nbExplorerClient.GetFeeRateAsync(blockCount, fallbackFeeRate, cancellation);
    }

    public async Task<MempoolRecommendedFees> GetMempoolRecommendedFeesAsync(
        CancellationToken cancellation = default)
    {
        var mempoolEndpoint = Constants.MEMPOOL_ENDPOINT;

        if (string.IsNullOrWhiteSpace(mempoolEndpoint))
            throw new Exception("MEMPOOL_ENDPOINT is not set");

        try
        {
            var recommendedFees =
                await _httpClient.GetFromJsonAsync<MempoolRecommendedFees>($"{mempoolEndpoint}/api/v1/fees/recommended");
            if (recommendedFees != null)
            {
                return recommendedFees;
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error getting mempool fees");
        }

        return new MempoolRecommendedFees();
    }

    public async Task<BroadcastResult> BroadcastAsync(Transaction tx, bool testMempoolAccept,
        CancellationToken cancellation = default)
    {
        var client = await LightningHelper.CreateNBExplorerClient();

        return await client.BroadcastAsync(tx, testMempoolAccept, cancellation);
    }

    public async Task ScanUTXOSetAsync(DerivationStrategyBase extKey, int? batchSize = null, int? gapLimit = null,
        int? fromIndex = null,
        CancellationToken cancellation = default)
    {
        var client = await LightningHelper.CreateNBExplorerClient();

        await client.ScanUTXOSetAsync(extKey, batchSize, gapLimit, fromIndex, cancellation);
    }

    public async Task<ScanUTXOInformation> GetScanUTXOSetInformationAsync(DerivationStrategyBase extKey,
        CancellationToken cancellation = default)
    {
        var client = await LightningHelper.CreateNBExplorerClient();

        return await client.GetScanUTXOSetInformationAsync(extKey, cancellation);
    }

    public async Task<StatusResult> GetStatusAsync(CancellationToken cancellation = default)
    {
        var client = await LightningHelper.CreateNBExplorerClient();

        var statusResult = await client.GetStatusAsync(cancellation);

        return statusResult;
    }
}