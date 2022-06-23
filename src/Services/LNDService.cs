using System.Text;
using FundsManager.Data.Models;
using FundsManager.Data.Repositories.Interfaces;
using Google.Protobuf;
using Grpc.Core;
using Lnrpc;
using Channel = FundsManager.Data.Models.Channel;

namespace FundsManager.Services
{
    public interface ILNDService
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
    public class LndService : ILNDService
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
                
            }

            var result = true;

            //TODO Log user approver 

            _logger.LogInformation("Channel open request for  request id:{} from node:{} to node:{}",
                channelOperationRequest.Id,
                source.Name,
                destination.Name);
            try
            {
                
                var openChannelResult = await _lightningClient.OpenChannelSyncAsync(new OpenChannelRequest
                {
                    //TODO Shim details for the PSBT
                    //FundingShim = new FundingShim
                    //{
                    //    PsbtShim = new PsbtShim
                    //    { BasePsbt = ByteString.Empty, NoPublish = false, PendingChanId = ByteString.Empty }
                    //},
                    //TODO Convert to satoshis with LightMoney
                    LocalFundingAmount = (long)channelOperationRequest.Amount,
                    //TODO Close address
                    //CloseAddress = "bc1...003"
                    Private = channelOperationRequest.IsChannelPrivate,
                    NodePubkey = ByteString.CopyFrom(Encoding.UTF8.GetBytes(destination.PubKey)),

                }, new Metadata{ {"macaroon", source.ChannelAdminMacaroon} }
                );

                _logger.LogInformation("Opened channel on channel point:{}:{} request id:{} from node:{} to node:{}",
                    openChannelResult.FundingTxidStr,
                    openChannelResult.OutputIndex,
                    channelOperationRequest.Id,
                    source.Name,
                    destination.Name);

                //Channel creation

                var channel = new Channel
                {
                    Capacity = channelOperationRequest.Amount,
                    //TODO Channel id retrieval it is not on the result from the open channel
                    FundingTx = openChannelResult.FundingTxidStr, //TODO Validate this data (?)
                    FundingTxOutputIndex = openChannelResult.OutputIndex,
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
