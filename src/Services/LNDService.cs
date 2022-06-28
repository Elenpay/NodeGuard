using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using FundsManager.Data.Models;
using FundsManager.Data.Repositories.Interfaces;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Lnrpc;
using Channel = FundsManager.Data.Models.Channel;

namespace FundsManager.Services
{
    public interface ILndService
    {
        /// <summary>
        /// Opens a channel with a completely signed PSBT from a node to another given node
        /// </summary>
        /// <param name="channelOperationRequest"></param>
        /// <param name="source"></param>
        /// <param name="destination"></param>
        /// <returns></returns>
        public Task<bool> OpenChannel(ChannelOperationRequest channelOperationRequest, Node source, Node destination);

        /// <summary>
        /// Based on a channel operation request of type close, requests the close of a channel to LND without acking the request
        /// </summary>
        /// <param name="channelOperationRequest"></param>
        /// <param name="forceClose"></param>
        /// <returns></returns>
        public Task<bool> CloseChannel(ChannelOperationRequest channelOperationRequest, bool forceClose = false);
    }

    /// <summary>
    /// Service in charge of communicating with LND over gRPC
    /// </summary>
    public class LndService : ILndService
    {
        private readonly Lightning.LightningClient _lightningClient;
        private readonly ILogger<LndService> _logger;
        private readonly IChannelRepository _channelRepository;
        private readonly IChannelOperationRequestRepository _channelOperationRequestRepository;

        public LndService(Lightning.LightningClient lightningClient, ILogger<LndService> logger, IChannelRepository channelRepository,
            IChannelOperationRequestRepository channelOperationRequestRepository)
        {
            _lightningClient = lightningClient;
            _logger = logger;
            _channelRepository = channelRepository;
            _channelOperationRequestRepository = channelOperationRequestRepository;
        }

        /// <summary>
        /// Opens a channel with a completely signed PSBT from a node to another given node
        /// </summary>
        /// <param name="channelOperationRequest"></param>
        /// <param name="source"></param>
        /// <param name="destination"></param>
        /// <returns></returns>
        public async Task<bool> OpenChannel(ChannelOperationRequest channelOperationRequest, Node source, Node destination)
        {
            if (channelOperationRequest == null) throw new ArgumentNullException(nameof(channelOperationRequest));
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (destination == null) throw new ArgumentNullException(nameof(destination));

            if (channelOperationRequest.RequestType != OperationRequestType.Open)
                return false;

            if (source.Id == destination.Id)
            {
                return false;
            }

            var httpHandler = new HttpClientHandler();
            // Return `true` to allow certificates that are untrusted/invalid
            httpHandler.ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;

            using var grpcChannel = GrpcChannel.ForAddress($"https://{source.Endpoint}",
                new GrpcChannelOptions { HttpHandler = httpHandler });
            
            var client = new Lightning.LightningClient(grpcChannel);

            var result = true;

            //TODO Log user approver 

            _logger.LogInformation("Channel open request for  request id:{} from node:{} to node:{}",
                channelOperationRequest.Id,
                source.Name,
                destination.Name);
            try
            {
                var openChannelRequest = new OpenChannelRequest
                {
                    //TODO Shim details for the PSBT
                    FundingShim = new FundingShim
                    {
                        PsbtShim = new PsbtShim
                        { BasePsbt = ByteString.Empty, NoPublish = false, PendingChanId = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32)) }
                    },
                    LocalFundingAmount = channelOperationRequest.SatsAmount,
                    //TODO Close address
                    //CloseAddress = "bc1...003"
                    Private = channelOperationRequest.IsChannelPrivate,
                    NodePubkey = ByteString.CopyFrom(Convert.FromHexString(destination.PubKey)),


                };

                //If PSBT mode is not enabled.. go the sync way
                var channelPoint
                    = new ChannelPoint();
                if (openChannelRequest.FundingShim == null)
                {
                    
                    channelPoint = await client.OpenChannelSyncAsync(openChannelRequest,
                        new Metadata {{"macaroon", source.ChannelAdminMacaroon}}
                    );

                    _logger.LogInformation("Opened channel on channel point:{}:{} request id:{} from node:{} to node:{}",
                        channelPoint.FundingTxidStr,
                        channelPoint.OutputIndex,
                        channelOperationRequest.Id,
                        source.Name,
                        destination.Name);

                    //Channel creation

                    //FundingTxidbytes to hex string

                    var fundingTxid = Convert.ToHexString(channelPoint.FundingTxidBytes.ToByteArray());

                    var channel = new Channel
                    {
                        Capacity = channelOperationRequest.SatsAmount,
                        //TODO Channel id retrieval it is not on the result from the open channel
                        FundingTx = fundingTxid, //TODO Validate this data (?)
                        FundingTxOutputIndex = channelPoint.OutputIndex,
                        CreationDatetime = DateTimeOffset.Now,
                        UpdateDatetime = DateTimeOffset.Now,
                        ChannelOperationRequests = new List<ChannelOperationRequest> { channelOperationRequest }
                        //TODO Set btc close address
                    };
                    var channelCreationResult = await _channelRepository.AddAsync(channel);

                    if (!channelCreationResult.Item1)
                    {
                        _logger.LogError("Error while saving channel entity for channel operation request with id:{} error:{}",
                            channelOperationRequest.Id, channelCreationResult.Item2);

                        if (channel.Id > 0)
                        {
                            channelOperationRequest.ChannelId = channel.Id;

                            _channelOperationRequestRepository.Update(channelOperationRequest);
                        }
                    }
                }
                else // Go to the async way with OpenStatusUpdate (PSBT)
                { 
                    var openStatusUpdateStream = client.OpenChannel(openChannelRequest,
                        new Metadata { { "macaroon", source.ChannelAdminMacaroon } }
                    );

                    await foreach (var response in openStatusUpdateStream.ResponseStream.ReadAllAsync())
                    {

                        switch (response.UpdateCase)
                        {
                            case OpenStatusUpdate.UpdateOneofCase.None:
                                break;
                            case OpenStatusUpdate.UpdateOneofCase.ChanPending:
                                break;
                            case OpenStatusUpdate.UpdateOneofCase.ChanOpen:
                                break;
                            case OpenStatusUpdate.UpdateOneofCase.PsbtFund:
                                
                                //We got the funding address

                                //Generation of the Base PSBT with the output

                                break;
                            default:
                                throw new ArgumentOutOfRangeException();
                        }

                        if (response?.PsbtFund?.FundingAddress != null)
                        {

                        }
                    }

                    //TODO Abandoning channels pending (?)
                }
                


            }

            catch (Exception e)
            {
                _logger.LogError(e,
                    "Channel open request failed for channel operation request:{} from node:{} to node:{}",
                    channelOperationRequest.Id,
                    source.Name,
                    destination.Name);
                result = false;
            }

            return result;

        }

        /// <summary>
        /// Based on a channel operation request of type close, requests the close of a channel to LND without acking the request
        /// </summary>
        /// <param name="channelOperationRequest"></param>
        /// <param name="forceClose"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentNullException"></exception>
        public async Task<bool> CloseChannel(ChannelOperationRequest channelOperationRequest, bool forceClose = false)
        {
            if (channelOperationRequest == null) throw new ArgumentNullException(nameof(channelOperationRequest));


            if (channelOperationRequest.RequestType != OperationRequestType.Close)
                return false;

            var result = true;

            _logger.LogInformation("Channel close request for request id:{}",
                channelOperationRequest.Id);

            try
            {
                if (channelOperationRequest.ChannelId != null)
                {
                    var channel = await _channelRepository.GetById(
                        channelOperationRequest.ChannelId.Value);

                    if (channel != null)
                    {
                        //Time to close the channel
                        var closeChannelResult = _lightningClient.CloseChannel(new CloseChannelRequest
                        {
                            ChannelPoint = new ChannelPoint
                            {
                                FundingTxidStr = channel.FundingTx,
                                OutputIndex = channel.FundingTxOutputIndex
                            },
                            Force = forceClose,

                        },new Metadata { { "macaroon", channelOperationRequest.SourceNode.ChannelAdminMacaroon } });

                        _logger.LogInformation("Channel close request:{} triggered",
                            channelOperationRequest.Id);

                        //TODO The closeChannelResult is a streaming with the status updates, this is an async long operation, maybe we should track this process elsewhere (?)


                    }


                }
            }
            catch (Exception e)
            {
                _logger.LogError(e,
                    "Channel close request failed for channel operation request:{}",
                    channelOperationRequest.Id);
                result = false;
            }

            return result;

        }
    }
}
