using AutoMapper;
using FundsManager.Data;
using FundsManager.Data.Models;
using FundsManager.Data.Repositories.Interfaces;
using FundsManager.Helpers;
using Google.Protobuf;
using Grpc.Core;
using Lnrpc;
using NBitcoin;
using NBXplorer.DerivationStrategy;
using NBXplorer.Models;
using Unmockable;
using Channel = FundsManager.Data.Models.Channel;
using Transaction = NBitcoin.Transaction;

namespace FundsManager.Services.ServiceHelpers;

public static class LightningServiceHelper
{
    public static PSBT GetCombinedPsbt(ChannelOperationRequest channelOperationRequest, ILogger? _logger = null)
    {
        //PSBT Combine
        var signedPsbts = channelOperationRequest.ChannelOperationRequestPsbts.Where(x =>
            !x.IsFinalisedPSBT && !x.IsInternalWalletPSBT && !x.IsTemplatePSBT);
        var signedPsbts2 = signedPsbts.Select(x => x.PSBT);

        var combinedPSBT = LightningHelper.CombinePSBTs(signedPsbts2, _logger);

        if (combinedPSBT != null) return combinedPSBT;

        var invalidPsbtNullToBeUsedForTheRequest = $"Invalid PSBT(null) to be used for the channel op request:{channelOperationRequest.Id}";
        _logger?.LogError(invalidPsbtNullToBeUsedForTheRequest);

        throw new ArgumentException(invalidPsbtNullToBeUsedForTheRequest, nameof(combinedPSBT));
    }

    public static async Task<KeyPathInformation?> GetCloseAddress(ChannelOperationRequest channelOperationRequest,
        DerivationStrategyBase derivationStrategyBase, INBXplorerService nbXplorerService, ILogger? _logger = null)
    {
        var closeAddress = await
            nbXplorerService.GetUnusedAsync(derivationStrategyBase, DerivationFeature.Deposit, 0, true, default);

        if (closeAddress != null) return closeAddress;

        var closeAddressNull = $"Closing address was null for an operation on wallet:{channelOperationRequest.Wallet.Id}";
        _logger?.LogError(closeAddressNull);

        throw new ArgumentException(closeAddressNull);
    }

    public static DerivationStrategyBase GetDerivationStrategyBase(ChannelOperationRequest channelOperationRequest, ILogger? _logger = null)
    {
        //Derivation strategy for the multisig address based on its wallet
        var derivationStrategyBase = channelOperationRequest.Wallet.GetDerivationStrategy();

        if (derivationStrategyBase != null) return derivationStrategyBase;

        var derivationNull = $"Derivation scheme not found for wallet:{channelOperationRequest.Wallet.Id}";

        _logger?.LogError(derivationNull);

        throw new ArgumentException(derivationNull);
    }

    public static void CheckArgumentsAreValid(ChannelOperationRequest channelOperationRequest, OperationRequestType requestype, ILogger? _logger = null)
    {
        if (channelOperationRequest == null) throw new ArgumentNullException(nameof(channelOperationRequest));

        if (channelOperationRequest.RequestType == requestype) return;

        string requestInvalid = $"Invalid request. Requested ${channelOperationRequest.RequestType.ToString()} on ${requestype.ToString()} method";

        _logger?.LogError(requestInvalid);

        throw new ArgumentOutOfRangeException(requestInvalid);
    }

    public static (Node, Node) CheckNodesAreValid(ChannelOperationRequest channelOperationRequest, ILogger? _logger = null)
    {
        var source = channelOperationRequest.SourceNode;
        var destination = channelOperationRequest.DestNode;

        if (source == null || destination == null)
        {
            throw new ArgumentException("Source or destination null", nameof(source));
        }

        if (source.ChannelAdminMacaroon == null)
        {
            throw new UnauthorizedAccessException("Macaroon not set for source channel");
        }

        if (source.PubKey != destination.PubKey) return (source, destination);

        const string aNodeCannotOpenAChannelToItself = "A node cannot open a channel to itself.";

        _logger?.LogError(aNodeCannotOpenAChannelToItself);

        throw new ArgumentException(aNodeCannotOpenAChannelToItself);
    }


    /// <summary>
    /// Aux method when the fundsmanager is the one in change of signing the PSBTs
    /// </summary>
    /// <param name="channelOperationRequest"></param>
    /// <param name="nbxplorerClient"></param>
    /// <param name="derivationStrategyBase"></param>
    /// <param name="channelfundingTx"></param>
    /// <param name="source"></param>
    /// <param name="client"></param>
    /// <param name="pendingChannelId"></param>
    /// <param name="network"></param>
    /// <param name="changeFixedPSBT"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentException"></exception>
    public static async Task<PSBT> SignPSBT(
        ChannelOperationRequest channelOperationRequest, INBXplorerService nbXplorerService,
        DerivationStrategyBase derivationStrategyBase, Transaction channelfundingTx, Network network, PSBT changeFixedPSBT, ILogger? logger = null)

    {
        //We get the UTXO keyPath / derivation path from nbxplorer

        var UTXOs = await nbXplorerService.GetUTXOsAsync(derivationStrategyBase, default);
        UTXOs.RemoveDuplicateUTXOs();

        var OutpointKeyPathDictionary =
            UTXOs.Confirmed.UTXOs.ToDictionary(x => x.Outpoint, x => x.KeyPath);

        var txInKeyPathDictionary =
            channelfundingTx.Inputs.Where(x => OutpointKeyPathDictionary.ContainsKey(x.PrevOut))
                .ToDictionary(x => x,
                    x => OutpointKeyPathDictionary[x.PrevOut]);

        if (!txInKeyPathDictionary.Any())
        {
            const string errorKeypathsForTheUtxosUsedInThisTxAreNotFound =
                "Error, keypaths for the UTXOs used in this tx are not found, probably this UTXO is already used as input of another transaction";

            logger?.LogError(errorKeypathsForTheUtxosUsedInThisTxAreNotFound);

            throw new ArgumentException(
                errorKeypathsForTheUtxosUsedInThisTxAreNotFound);
        }


        Dictionary<NBitcoin.OutPoint, NBitcoin.Key> privateKeysForUsedUTXOs;
        if (channelOperationRequest.Wallet.IsHotWallet)
        {
            try
            {
                privateKeysForUsedUTXOs = txInKeyPathDictionary.ToDictionary(x => x.Key.PrevOut, x => channelOperationRequest.Wallet.InternalWallet.GetAccountKey(network)
                    .Derive(UInt32.Parse(channelOperationRequest.Wallet.InternalWalletSubDerivationPath))
                    .Derive(x.Value)
                    .PrivateKey);
            }
            catch (Exception e)
            {
                var errorParsingSubderivationPath =
                    $"Invalid Internal Wallet Subderivation Path for wallet:{channelOperationRequest.WalletId}";
                logger?.LogError(errorParsingSubderivationPath);

                throw new ArgumentException(
                    errorParsingSubderivationPath);
            }
        }
        else
        {
            privateKeysForUsedUTXOs = txInKeyPathDictionary.ToDictionary(x => x.Key.PrevOut, x => channelOperationRequest.Wallet.InternalWallet.GetAccountKey(network)
                .Derive(x.Value)
                .PrivateKey);
        }

        //We need to SIGHASH_ALL all inputs/outputs as fundsmanager to protect the tx from tampering by adding a signature
        var partialSigsCount = changeFixedPSBT.Inputs.Sum(x => x.PartialSigs.Count);
        foreach (var input in changeFixedPSBT.Inputs)
        {
            if (privateKeysForUsedUTXOs.TryGetValue(input.PrevOut, out var key))
            {
                input.Sign(key);
            }
        }

        //We check that the partial signatures number has changed, otherwise finalize inmediately
        var partialSigsCountAfterSignature =
            changeFixedPSBT.Inputs.Sum(x => x.PartialSigs.Count);

        if (partialSigsCountAfterSignature == 0 ||
            partialSigsCountAfterSignature <= partialSigsCount)
        {
            var invalidNoOfPartialSignatures =
                $"Invalid expected number of partial signatures after signing for the channel operation request:{channelOperationRequest.Id}";
            logger?.LogError(invalidNoOfPartialSignatures);

            throw new ArgumentException(
                invalidNoOfPartialSignatures);
        }

        return changeFixedPSBT;
    }

    /// <summary>
    /// Cancels a pending channel from LND PSBT-based funding of channels
    /// </summary>
    /// <param name="source"></param>
    /// <param name="client"></param>
    /// <param optional="true" name="logger"></param>
    /// <param name="pendingChannelId"></param>
    public static void CancelPendingChannel(Node source, IUnmockable<Lightning.LightningClient> client, byte[] pendingChannelId, ILogger logger = null)
    {
        try
        {
            if (pendingChannelId != null)
            {
                var cancelRequest = new FundingShimCancel
                {
                    PendingChanId = ByteString.CopyFrom(pendingChannelId)
                };

                if (source.ChannelAdminMacaroon != null)
                {
                    var cancelResult = client.Execute(x => x.FundingStateStep(new FundingTransitionMsg
                        {
                            ShimCancel = cancelRequest,
                        },
                        new Metadata { { "macaroon", source.ChannelAdminMacaroon } }, null, default));
                }
            }
        }
        catch (Exception e)
        {
            logger?.LogError(e, "Error while cancelling pending channel with id: {ChannelId} (hex)",
                Convert.ToHexString(pendingChannelId));
        }
    }


    /// <summary>
    /// Gets UTXOs confirmed from the wallet of the request
    /// </summary>
    /// <param name="channelOperationRequest"></param>
    /// <param name="nbxplorerClient"></param>
    /// <param name="derivationStrategy"></param>
    /// <returns></returns>
    public static async Task<(List<ScriptCoin> coins, List<UTXO> selectedUTXOs)> GetTxInputCoins(
        ChannelOperationRequest channelOperationRequest,
        INBXplorerService nbXplorerService,
        DerivationStrategyBase derivationStrategy,
        IFMUTXORepository ifmutxoRepository,
        ILogger logger,
        IMapper mapper)
    {
        var utxoChanges = await nbXplorerService.GetUTXOsAsync(derivationStrategy, default);
        utxoChanges.RemoveDuplicateUTXOs();

        var satsAmount = channelOperationRequest.SatsAmount;
        var lockedUTXOs = await ifmutxoRepository.GetLockedUTXOs(ignoredChannelOperationRequestId: channelOperationRequest.Id);

        var (coins, selectedUTXOs) = await LightningHelper.SelectCoins(channelOperationRequest.Wallet,
            satsAmount,
            utxoChanges,
            lockedUTXOs,
            logger,
            mapper);

        return (coins, selectedUTXOs);
    }

    public static void HandleChannelPending(ChannelOperationRequest channelOperationRequest, string pendingChannelIdHex, OpenStatusUpdate response, IChannelOperationRequestRepository channelOperationRequestRepository, ILogger<LightningService>? logger = null)
    {
        //Channel funding tx on mempool and pending status on lnd

        logger?.LogInformation(
            "Channel pending for channel operation request id: {RequestId} for pending channel id: {ChannelId}",
            channelOperationRequest.Id, pendingChannelIdHex);

        channelOperationRequest.Status = ChannelOperationRequestStatus.OnChainConfirmationPending;
        channelOperationRequest.TxId = LightningHelper.DecodeTxId(response.ChanPending.Txid);
        channelOperationRequestRepository.Update(channelOperationRequest);
    }

    public static async Task HandleChannelOpen(ChannelOperationRequest channelOperationRequest, OpenStatusUpdate response, IUnmockable<Lightning.LightningClient> client, Node source, KeyPathInformation? closeAddress, ApplicationDbContext context, IChannelOperationRequestRepository channelOperationRequestRepository, ILogger<LightningService>? logger = null)
    {
        logger.LogInformation(
            "Channel opened for channel operation request request id: {RequestId}, channel point: {ChannelPoint}",
            channelOperationRequest.Id, response.ChanOpen.ChannelPoint.ToString());

        channelOperationRequest.Status = ChannelOperationRequestStatus.OnChainConfirmed;
        channelOperationRequestRepository.Update(channelOperationRequest);

        var fundingTx = LightningHelper.DecodeTxId(response.ChanOpen.ChannelPoint.FundingTxidBytes);

        //Get the channels to find the channelId, not the temporary one
        var channels = await client.Execute(x => x.ListChannelsAsync(new ListChannelsRequest(),
            new Metadata { { "macaroon", source.ChannelAdminMacaroon } }, null, default));
        var currentChannel = channels.Channels.SingleOrDefault(x => x.ChannelPoint == $"{fundingTx}:{response.ChanOpen.ChannelPoint.OutputIndex}");

        if (currentChannel == null)
        {
            logger.LogError("Error, channel not found for channel point: {ChannelPoint}",
                response.ChanOpen.ChannelPoint.ToString());
            throw new InvalidOperationException();
        }

        var channel = new Channel
        {
            ChanId = currentChannel.ChanId,
            CreationDatetime = DateTimeOffset.Now,
            FundingTx = fundingTx,
            FundingTxOutputIndex = response.ChanOpen.ChannelPoint.OutputIndex,
            BtcCloseAddress = closeAddress?.Address.ToString(),
            SatsAmount = channelOperationRequest.SatsAmount,
            UpdateDatetime = DateTimeOffset.Now,
            Status = Channel.ChannelStatus.Open,
            NodeId = channelOperationRequest.SourceNode.Id
        };

        await context.AddAsync(channel);

        var addChannelResult = (await context.SaveChangesAsync()) > 0;

        if (addChannelResult == false)
        {
            logger.LogError(
                "Channel for channel operation request id: {RequestId} could not be created, reason: {Reason}",
                channelOperationRequest.Id,
                "Could not persist to db");
        }

        channelOperationRequest.ChannelId = channel.Id;
        channelOperationRequest.DestNode = null;

        var channelUpdate = channelOperationRequestRepository.Update(channelOperationRequest);

        if (channelUpdate.Item1 == false)
        {
            logger.LogError(
                "Could not assign channel id to channel operation request: {RequestId} reason: {Reason}",
                channelOperationRequest.Id,
                channelUpdate.Item2);
        }
    }

    public static async Task HandlePSBTFund(ChannelOperationRequest channelOperationRequest, byte[] pendingChannelId,OpenStatusUpdate response, Network network, PSBT combinedPSBT, 
        IUnmockable<Lightning.LightningClient> client, Node source, DerivationStrategyBase derivationStrategyBase, IRemoteSignerService remoteSignerService, INBXplorerService nbXplorerService,
        IChannelOperationRequestPSBTRepository channelOperationRequestPsbtRepository, IChannelOperationRequestRepository channelOperationRequestRepository, ILogger logger)
    {
        //We got the funded PSBT, we need to tweak the tx outputs and mimick lnd-cli calls
        var hexPSBT = Convert.ToHexString((response.PsbtFund.Psbt.ToByteArray()));
        if (PSBT.TryParse(hexPSBT, network,
                out var fundedPSBT))
        {
            fundedPSBT.AssertSanity();

            //We ensure to SigHash.None
            fundedPSBT.Settings.SigningOptions = new SigningOptions
            {
                SigHash = SigHash.None
            };

            var channelfundingTx = fundedPSBT.GetGlobalTransaction();

            //We manually fix the change (it was wrong from the Base template due to nbitcoin requiring a change on a PSBT)

            var totalIn = new Money(0L);

            foreach (var input in fundedPSBT.Inputs)
            {
                totalIn += (input.GetTxOut()?.Value);
            }

            var totalOut = new Money(channelOperationRequest.SatsAmount, MoneyUnit.Satoshi);
            var totalFees = combinedPSBT.GetFee();
            channelfundingTx.Outputs[0].Value = totalIn - totalOut - totalFees;

            //We merge changeFixedPSBT with the other PSBT with the change fixed

            var changeFixedPSBT = channelfundingTx.CreatePSBT(network).UpdateFrom(fundedPSBT);

            PSBT? finalSignedPSBT = null;
            //We check the way the fundsmanager signs, with the remoteFundsManagerSigner or by itself.
            if (Constants.ENABLE_REMOTE_SIGNER)
            {
                finalSignedPSBT = await remoteSignerService.Sign(changeFixedPSBT);
                if (finalSignedPSBT == null)
                {
                    const string errorMessage = "The signed PSBT was null, something went wrong while signing with the remote signer";
                    logger.LogError(errorMessage);
                    throw new Exception(
                        errorMessage);
                }
            }
            else
            {
                finalSignedPSBT = await SignPSBT(channelOperationRequest,
                    nbXplorerService,
                    derivationStrategyBase,
                    channelfundingTx,
                    network,
                    changeFixedPSBT,
                    logger);

                if (finalSignedPSBT == null)
                {
                    const string errorMessage = "The signed PSBT was null, something went wrong while signing with the embedded signer";
                    logger.LogError(errorMessage);
                    throw new Exception(
                        errorMessage);
                }
            }

            //We store the final signed PSBT without being finalized for debugging purposes
            var signedChannelOperationRequestPsbt = new ChannelOperationRequestPSBT
            {
                ChannelOperationRequestId = channelOperationRequest.Id,
                PSBT = finalSignedPSBT.ToBase64(),
                CreationDatetime = DateTimeOffset.Now,
                IsInternalWalletPSBT = true
            };

            var addResult = await channelOperationRequestPsbtRepository.AddAsync(signedChannelOperationRequestPsbt);

            if (!addResult.Item1)
            {
                logger.LogError("Could not store the signed PSBT for channel operation request id: {RequestId} reason: {Reason}", channelOperationRequest.Id, addResult.Item2);
            }

            //Time to finalize the PSBT and broadcast the tx

            var finalizedPSBT = finalSignedPSBT.Finalize();

            //Sanity check
            finalizedPSBT.AssertSanity();

            channelfundingTx = finalizedPSBT.ExtractTransaction();

            //Just a check of the tx based on the finalizedPSBT
            var checkTx = channelfundingTx.Check();

            if (checkTx == TransactionCheckResult.Success)
            {
                //We tell lnd to verify the psbt
                client.Execute(x => x.FundingStateStep(
                    new FundingTransitionMsg
                    {
                        PsbtVerify = new FundingPsbtVerify
                        {
                            FundedPsbt =
                                ByteString.CopyFrom(Convert.FromHexString(finalizedPSBT.ToHex())),
                            PendingChanId = ByteString.CopyFrom(pendingChannelId)
                        }
                    }, new Metadata { { "macaroon", source.ChannelAdminMacaroon } }, null, default));

                //Saving the PSBT in the ChannelOperationRequest collection of PSBTs

                channelOperationRequest =
                    await channelOperationRequestRepository.GetById(channelOperationRequest.Id) ?? throw new InvalidOperationException();

                if (channelOperationRequest.ChannelOperationRequestPsbts != null)
                {
                    var finalizedChannelOperationRequestPsbt = new ChannelOperationRequestPSBT
                    {
                        IsFinalisedPSBT = true,
                        CreationDatetime = DateTimeOffset.Now,
                        PSBT = finalizedPSBT.ToBase64(),
                        ChannelOperationRequestId = channelOperationRequest.Id
                    };

                    var finalisedPSBTAdd = await
                        channelOperationRequestPsbtRepository.AddAsync(finalizedChannelOperationRequestPsbt);

                    if (!finalisedPSBTAdd.Item1)
                    {
                        logger.LogError(
                            "Error while saving the finalised PSBT for channel operation request with id: {RequestId}",
                            channelOperationRequest.Id);
                    }
                }

                var fundingStateStepResp = await client.Execute(x => x.FundingStateStepAsync(
                    new FundingTransitionMsg
                    {
                        PsbtFinalize = new FundingPsbtFinalize
                        {
                            PendingChanId = ByteString.CopyFrom(pendingChannelId),
                            //FinalRawTx = ByteString.CopyFrom(Convert.FromHexString(finalTxHex)),
                            SignedPsbt =
                                ByteString.CopyFrom(Convert.FromHexString(finalizedPSBT.ToHex()))
                        },
                    }, new Metadata { { "macaroon", source.ChannelAdminMacaroon } }, null, default));
            }
            else
            {
                CancelPendingChannel(source, client, pendingChannelId, logger);
            }
        }
        else
        {
            CancelPendingChannel(source, client, pendingChannelId, logger);
        }
    }
}