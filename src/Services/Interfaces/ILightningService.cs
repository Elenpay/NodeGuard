using FundsManager.Data.Models;
using Lnrpc;
using NBitcoin;
using NBXplorer.DerivationStrategy;
using NBXplorer.Models;

namespace FundsManager.Services.Interfaces;

/// <summary>
/// Service to interact with LND
/// </summary>
public interface ILightningService
{
    /// <summary>
    /// Opens a channel based on a request this method waits for I/O on the blockchain, therefore it can last its execution for minutes
    /// </summary>
    /// <param name="channelOperationRequest"></param>
    /// <returns></returns>
    // ReSharper disable once IdentifierTypo
    public Task OpenChannel(ChannelOperationRequest channelOperationRequest);

    /// <summary>
    /// Generates a template PSBT with Sighash_NONE and some UTXOs from the wallet related to the request without signing, also returns if there are no utxos available at the request time
    /// </summary>
    /// <param name="channelOperationRequest"></param>
    /// <param name="destinationAddress"></param>
    /// <returns></returns>
    public Task<(PSBT?, bool)> GenerateTemplatePSBT(ChannelOperationRequest channelOperationRequest);

    /// <summary>
    /// Based on a channel operation request of type close, requests the close of a channel to LND without acking the request.
    /// This method waits for I/O on the blockchain, therefore it can last its execution for minutes
    /// </summary>
    /// <param name="channelOperationRequest"></param>
    /// <param name="forceClose"></param>
    /// <returns></returns>
    public Task CloseChannel(ChannelOperationRequest channelOperationRequest, bool forceClose = false);

    /// <summary>
    /// Gets the wallet balance
    /// </summary>
    /// <param name="wallet"></param>
    /// <returns></returns>
    public Task<GetBalanceResponse?> GetWalletBalance(Wallet wallet);

    /// <summary>
    /// Gets the wallet balance
    /// </summary>
    /// <param name="wallet"></param>
    /// <param name="derivationFeature"></param>
    /// <returns></returns>
    public Task<BitcoinAddress?> GetUnusedAddress(Wallet wallet,
        DerivationFeature derivationFeature);

    /// <summary>
    /// Gets the info about a node in the lightning network graph
    /// </summary>
    /// <param name="pubkey"></param>
    /// <returns></returns>
    public Task<LightningNode?> GetNodeInfo(string pubkey);
}
