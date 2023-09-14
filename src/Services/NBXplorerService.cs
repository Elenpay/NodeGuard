using System.Text.Json;
using NodeGuard.Helpers;
using Microsoft.AspNetCore.WebUtilities;
using NBitcoin;
using NBXplorer;
using NBXplorer.DerivationStrategy;
using NBXplorer.Models;
using Newtonsoft.Json;

namespace NodeGuard.Services;

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

    public Task<UTXOChanges> GetUTXOsByLimitAsync(DerivationStrategyBase extKey, CoinSelectionStrategy strategy = CoinSelectionStrategy.SmallestFirst, int limit = 0, long amount = 0, long closestTo = 0, List<string>? ignoreOutpoints = null, CancellationToken cancellation = default);

    public Task<GetFeeRateResult> GetFeeRateAsync(int blockCount, FeeRate fallbackFeeRate,
        CancellationToken cancellation = default);

    public Task<decimal?> GetFeesByType(MempoolRecommendedFeesTypes mempoolRecommendedFeesTypes, CancellationToken cancellation = default);

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
    public decimal FastestFee { get; set; }
    public decimal HalfHourFee { get; set; }
    public decimal HourFee { get; set; }
    public decimal EconomyFee { get; set; }
    public decimal MinimumFee { get; set; }
}

[JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum CoinSelectionStrategy
{
    SmallestFirst,
    BiggestFirst,
    ClosestToTargetFirst,
    UpToAmount
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

    public async Task<UTXOChanges> GetUTXOsByLimitAsync(DerivationStrategyBase extKey,
        CoinSelectionStrategy strategy = CoinSelectionStrategy.SmallestFirst,
        int limit = 0,
        long amount = 0,
        long closestTo = 0,
        List<string>? ignoreOutpoints = null,
        CancellationToken cancellation = default)
    {
        try
        {
            var requestUri = $"{Constants.NBXPLORER_URI}/v1/cryptos/btc/derivations/{TrackedSource.Create(extKey).DerivationStrategy}/selectutxos";

            var keyValuePairs = new List<KeyValuePair<string, string?>>()
            {
                new("strategy", strategy.ToString()),
                new("limit", limit.ToString()),
                new("amount", amount.ToString()),
            };
            if (strategy == CoinSelectionStrategy.ClosestToTargetFirst)
            {
                keyValuePairs.Add(new("closestTo", closestTo.ToString()));
            }

            ignoreOutpoints?.ForEach(outpoint => keyValuePairs.Add(new("ignoreOutpoint", outpoint)));

            var url = QueryHelpers.AddQueryString(requestUri, keyValuePairs);
            var response = await _httpClient.GetAsync(url, cancellation);

            if (response.IsSuccessStatusCode)
            {
                var client = await LightningHelper.CreateNBExplorerClient();

                return client.Serializer.ToObject<UTXOChanges>(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e.ToString());
            throw e;
        }

        return new UTXOChanges();
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

    private async Task<MempoolRecommendedFees> GetMempoolRecommendedFeesAsync(
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

    public async Task<decimal?> GetFeesByType(
        MempoolRecommendedFeesTypes mempoolRecommendedFeesTypes,
        CancellationToken cancellation = default)
    {
        var recommendedFees = await GetMempoolRecommendedFeesAsync(cancellation);

        switch (mempoolRecommendedFeesTypes)
        {
            case MempoolRecommendedFeesTypes.EconomyFee:
                return recommendedFees.EconomyFee;
            case MempoolRecommendedFeesTypes.FastestFee:
                return recommendedFees.FastestFee;
            case MempoolRecommendedFeesTypes.HourFee:
                return recommendedFees.HourFee;
            case MempoolRecommendedFeesTypes.HalfHourFee:
                return recommendedFees.HalfHourFee;
            case MempoolRecommendedFeesTypes.CustomFee:
                return null;
        }

        throw new Exception("Invalid mempoolRecommendedFeesTypes");
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