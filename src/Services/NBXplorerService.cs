using FundsManager.Helpers;
using NBitcoin;
using NBXplorer.DerivationStrategy;
using NBXplorer.Models;

namespace FundsManager.Services;


public interface INBXplorerService
{
  public Task TrackAsync(DerivationStrategyBase derivationStrategyBase, CancellationToken cancellation = default);

  public Task TrackAsync(TrackedSource trackedSource, CancellationToken cancellation = default);
  
  public Task<TransactionResult?> GetTransactionAsync(uint256 txId,CancellationToken cancellation = default);
  
  public Task<KeyPathInformation?> GetUnusedAsync(DerivationStrategyBase strategy, DerivationFeature feature, int skip = 0, bool reserve = false,CancellationToken cancellation = default);

  public Task<GetBalanceResponse> GetBalanceAsync(DerivationStrategyBase userDerivationScheme,CancellationToken cancellation = default);
  
  public Task<StatusResult> GetStatusAsync(CancellationToken cancellation = default);

  public Task<UTXOChanges> GetUTXOsAsync(DerivationStrategyBase extKey, CancellationToken cancellation = default);

  public Task<GetFeeRateResult> GetFeeRateAsync(int blockCount, FeeRate fallbackFeeRate,
    CancellationToken cancellation = default);


  public Task<BroadcastResult> BroadcastAsync(Transaction tx, bool testMempoolAccept,
    CancellationToken cancellation = default);
  
  public Task ScanUTXOSetAsync( DerivationStrategyBase extKey, 
    int? batchSize = null, 
    int? gapLimit = null, 
    int? fromIndex = null, 
    CancellationToken cancellation = default(CancellationToken));

  public Task<ScanUTXOInformation> GetScanUTXOSetInformationAsync(DerivationStrategyBase extKey,
    CancellationToken cancellation = default(CancellationToken));


}   
/// <summary>
/// Wrapper for the NBXplorer client to support DI
/// </summary>
public class NBXplorerService : INBXplorerService
{
  public async Task TrackAsync(DerivationStrategyBase derivationStrategyBase, CancellationToken cancellation = default)
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

  public async Task<GetBalanceResponse> GetBalanceAsync(DerivationStrategyBase userDerivationScheme, CancellationToken cancellation = default)
  {
    var client = await LightningHelper.CreateNBExplorerClient();
    
    return await client.GetBalanceAsync(userDerivationScheme, cancellation);
   }



  public async Task<UTXOChanges> GetUTXOsAsync(DerivationStrategyBase extKey, CancellationToken cancellation = default)
  {
    var client = await LightningHelper.CreateNBExplorerClient();
    
    return await client.GetUTXOsAsync(extKey, cancellation);
  }

  public async 
    Task<GetFeeRateResult> GetFeeRateAsync(int blockCount, FeeRate fallbackFeeRate, CancellationToken cancellation = default)
  {
    var client = await LightningHelper.CreateNBExplorerClient();
    
    return await client.GetFeeRateAsync(blockCount, fallbackFeeRate, cancellation);
  }

  public async Task<BroadcastResult> BroadcastAsync(Transaction tx, bool testMempoolAccept, CancellationToken cancellation = default)
  {
    var client = await LightningHelper.CreateNBExplorerClient();
    
    return await client.BroadcastAsync(tx, testMempoolAccept, cancellation);
  }

  public async Task ScanUTXOSetAsync(DerivationStrategyBase extKey, int? batchSize = null, int? gapLimit = null, int? fromIndex = null,
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