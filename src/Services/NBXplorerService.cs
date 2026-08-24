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

    public Task TrackAsync(TrackedSource trackedSource, TrackWalletRequest trackWalletRequest, CancellationToken cancellation = default);

    public Task<TransactionResult?> GetTransactionAsync(uint256 txId, CancellationToken cancellation = default);

    public Task<KeyPathInformation?> GetUnusedAsync(DerivationStrategyBase strategy, DerivationFeature feature,
        int skip = 0, bool reserve = false, CancellationToken cancellation = default);

    public Task<GetBalanceResponse> GetBalanceAsync(DerivationStrategyBase userDerivationScheme,
        CancellationToken cancellation = default);

    public Task<StatusResult> GetStatusAsync(CancellationToken cancellation = default);

    public Task<UTXOChanges> GetUTXOsAsync(DerivationStrategyBase derivationStrategy, CancellationToken cancellation = default);

    public Task<UTXOChanges> GetUTXOsByLimitAsync(DerivationStrategyBase derivationStrategy, CoinSelectionStrategy strategy = CoinSelectionStrategy.SmallestFirst, int limit = 0, long amount = 0, long closestTo = 0, List<string>? ignoreOutpoints = null, CancellationToken cancellation = default);

    public Task<GetFeeRateResult> GetFeeRateAsync(int blockCount, FeeRate fallbackFeeRate,
        CancellationToken cancellation = default);

    public Task<decimal?> GetFeesByType(MempoolRecommendedFeesType mempoolRecommendedFeesType, CancellationToken cancellation = default);

    public Task<BroadcastResult> BroadcastAsync(Transaction tx, bool testMempoolAccept,
        CancellationToken cancellation = default);

    public Task<GetTransactionsResponse> GetTransactionsAsync(DerivationStrategyBase derivationStrategy);

    public Task ScanUTXOSetAsync(DerivationStrategyBase derivationStrategy,
        int? batchSize = null,
        int? gapLimit = null,
        int? fromIndex = null,
        CancellationToken cancellation = default(CancellationToken));

    public Task<ScanUTXOInformation> GetScanUTXOSetInformationAsync(DerivationStrategyBase derivationStrategy,
        CancellationToken cancellation = default(CancellationToken));
}

public enum MempoolRecommendedFeesType
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

    public async Task TrackAsync(TrackedSource trackedSource, TrackWalletRequest trackWalletRequest, CancellationToken cancellation = default)
    {
        var client = await LightningHelper.CreateNBExplorerClient();
        await client.TrackAsync(trackedSource, trackWalletRequest, cancellation);
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


    public async Task<UTXOChanges> GetUTXOsAsync(DerivationStrategyBase derivationStrategy,
        CancellationToken cancellation = default)
    {
        var client = await LightningHelper.CreateNBExplorerClient();

        return await client.GetUTXOsAsync(derivationStrategy, cancellation);
    }

    public async Task<UTXOChanges> GetUTXOsByLimitAsync(DerivationStrategyBase derivationStrategy,
        CoinSelectionStrategy strategy = CoinSelectionStrategy.SmallestFirst,
        int limit = 0,
        long amount = 0,
        long closestTo = 0,
        List<string>? ignoreOutpoints = null,
        CancellationToken cancellation = default)
    {
        try
        {
            var requestUri = $"{Constants.NBXPLORER_URI}/v1/cryptos/btc/derivations/{TrackedSource.Create(derivationStrategy).DerivationStrategy}/selectutxos";

            var keyValuePairs = new List<KeyValuePair<string, string?>>()
            {
                new("strategy", strategy.ToString()),
                new("limit", limit.ToString()),
                new("amount", amount.ToString()),
                // Lets the backend drop dust by value, so callers do not have to spend one query
                // parameter per dust outpoint saying the same thing
                new("minimumValue", Constants.MINIMUM_UTXO_VALUE_SATS.ToString()),
            };
            if (strategy == CoinSelectionStrategy.ClosestToTargetFirst)
            {
                keyValuePairs.Add(new("closestTo", closestTo.ToString()));
            }

            var url = QueryHelpers.AddQueryString(requestUri, keyValuePairs);

            // The ignored outpoints travel in the body. As one repeated ignoreOutpoint query
            // parameter each they cost ~83 bytes apiece, so about ninety of them overflowed
            // Kestrel's 8KB MaxRequestLineSize and NBXplorer answered 414 — which the callers
            // swallow into a degraded selection. The body limit is 30MB, roughly 3,662x the
            // room, so the list no longer has a practical ceiling.
            // NOTE: this requires an NBXplorer serving POST /selectutxos (Elenpay/NBXplorer#18).
            // Against an older build every call answers 405 and coin selection degrades.
            var response = await _httpClient.PostAsync(url,
                JsonContent.Create(new { ignoreOutpoints }), cancellation);

            if (response.IsSuccessStatusCode)
            {
                var client = await LightningHelper.CreateNBExplorerClient();

                return client.Serializer.ToObject<UTXOChanges>(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            }

            throw new HttpRequestException(
                $"selectutxos request failed with status code {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false)}");
        }
        catch (Exception e)
        {
            _logger.LogError(e.ToString());
            throw;
        }
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
                    FeeRate = new FeeRate((decimal)recommendedFees.FastestFee),
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
        MempoolRecommendedFeesType mempoolRecommendedFeesType,
        CancellationToken cancellation = default)
    {
        var recommendedFees = await GetMempoolRecommendedFeesAsync(cancellation);

        switch (mempoolRecommendedFeesType)
        {
            case MempoolRecommendedFeesType.EconomyFee:
                return recommendedFees.EconomyFee;
            case MempoolRecommendedFeesType.FastestFee:
                return recommendedFees.FastestFee;
            case MempoolRecommendedFeesType.HourFee:
                return recommendedFees.HourFee;
            case MempoolRecommendedFeesType.HalfHourFee:
                return recommendedFees.HalfHourFee;
            case MempoolRecommendedFeesType.CustomFee:
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

    public async Task<GetTransactionsResponse> GetTransactionsAsync(DerivationStrategyBase derivationStrategy)
    {
        var client = await LightningHelper.CreateNBExplorerClient();

        return await client.GetTransactionsAsync(derivationStrategy);
    }

    public async Task ScanUTXOSetAsync(DerivationStrategyBase derivationStrategy, int? batchSize = null, int? gapLimit = null,
        int? fromIndex = null,
        CancellationToken cancellation = default)
    {
        var client = await LightningHelper.CreateNBExplorerClient();

        await client.ScanUTXOSetAsync(derivationStrategy, batchSize, gapLimit, fromIndex, cancellation);
    }

    public async Task<ScanUTXOInformation> GetScanUTXOSetInformationAsync(DerivationStrategyBase derivationStrategy,
        CancellationToken cancellation = default)
    {
        var client = await LightningHelper.CreateNBExplorerClient();

        return await client.GetScanUTXOSetInformationAsync(derivationStrategy, cancellation);
    }

    public async Task<StatusResult> GetStatusAsync(CancellationToken cancellation = default)
    {
        var client = await LightningHelper.CreateNBExplorerClient();

        var statusResult = await client.GetStatusAsync(cancellation);

        return statusResult;
    }
}