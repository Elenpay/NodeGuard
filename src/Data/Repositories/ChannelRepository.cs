using FundsManager.Data.Models;
using FundsManager.Data.Repositories.Interfaces;
using FundsManager.Services;
using Microsoft.EntityFrameworkCore;

namespace FundsManager.Data.Repositories
{
    public class ChannelRepository : IChannelRepository
    {
        private readonly IRepository<Channel> _repository;
        private readonly ILogger<ChannelRepository> _logger;
        private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;
        private readonly ILightningService _lightningService;
        private readonly IChannelOperationRequestRepository _channelOperationRequestRepository;

        public ChannelRepository(IRepository<Channel> repository,
            ILogger<ChannelRepository> logger,
            IDbContextFactory<ApplicationDbContext> dbContextFactory,
            ILightningService lightningService,
            IChannelOperationRequestRepository channelOperationRequestRepository
            )
        {
            _repository = repository;
            _logger = logger;
            _dbContextFactory = dbContextFactory;
            _lightningService = lightningService;
            _channelOperationRequestRepository = channelOperationRequestRepository;
        }

        public async Task<Channel?> GetById(int id)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await applicationDbContext.Channels.FirstOrDefaultAsync(x => x.Id == id);
        }

        public async Task<List<Channel>> GetAll()
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await applicationDbContext.Channels
                .Include(channel => channel.ChannelOperationRequests).ThenInclude(request => request.User)
                .Include(channel => channel.ChannelOperationRequests).ThenInclude(request => request.SourceNode)
                .Include(channel => channel.ChannelOperationRequests).ThenInclude(request => request.Wallet)
                .Include(channel => channel.ChannelOperationRequests).ThenInclude(request => request.DestNode)
                .Include(channel => channel.ChannelOperationRequests).ThenInclude(request => request.ChannelOperationRequestPsbts)
                .ToListAsync();
        }

        public async Task<(bool, string?)> AddAsync(Channel type)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await _repository.AddAsync(type, applicationDbContext);
        }

        public async Task<(bool, string?)> AddRangeAsync(List<Channel> type)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await _repository.AddRangeAsync(type, applicationDbContext);
        }

        public (bool, string?) Remove(Channel type)
        {
            using var applicationDbContext = _dbContextFactory.CreateDbContext();

            return _repository.Remove(type, applicationDbContext);
        }

        public async Task<(bool, string?)> SafeRemove(Channel type, bool forceClose = false)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();
            var openRequest = applicationDbContext.ChannelOperationRequests.Include(x => x.SourceNode).SingleOrDefault(request => request.ChannelId == type.Id);
            var closeRequest = new ChannelOperationRequest
            {
                ChannelId = type.Id,
                RequestType = OperationRequestType.Close,
                CreationDatetime = DateTimeOffset.Now,
                UpdateDatetime = DateTimeOffset.Now,
                Status = ChannelOperationRequestStatus.Approved, // Close operations are pre-approved by default
                Description = "Close Channel Operation for Channel " + type.Id,
            };
            if (openRequest != null)
            {
                closeRequest.WalletId = openRequest.WalletId;
                closeRequest.SourceNodeId = openRequest.SourceNodeId;

                closeRequest.DestNodeId = openRequest.DestNodeId;
                closeRequest.AmountCryptoUnit = openRequest.AmountCryptoUnit;
                closeRequest.SatsAmount = openRequest.SatsAmount;
                closeRequest.UserId = openRequest.UserId;
            }
            else
            {
                _logger.LogWarning("Could not find a opening request operation for this channel Some of the fields will be not set");
            }

            var closeRequestAddResult = await _channelOperationRequestRepository.AddAsync(closeRequest);

            if (!closeRequestAddResult.Item1)
            {
                _logger.LogError("Error while saving close request for channel with id:{}", type.Id);
                return (false, closeRequestAddResult.Item2);
            }

            closeRequest = await _channelOperationRequestRepository.GetById(closeRequest.Id);

            //LND Closing has to be done with a proxy service avoid cycles in the dependency injection
            var closeResult = await _lightningService.CloseChannel(closeRequest, forceClose);

            if (!closeResult)
            {
                _logger.LogError("Channel with id:{} could not be closed", closeRequest.Channel.Id);
                return (false, "Channel could not be closed");
            }

            return (true, null);
        }

        public (bool, string?) RemoveRange(List<Channel> types)
        {
            using var applicationDbContext = _dbContextFactory.CreateDbContext();

            return _repository.RemoveRange(types, applicationDbContext);
        }

        public (bool, string?) Update(Channel type)
        {
            using var applicationDbContext = _dbContextFactory.CreateDbContext();

            return _repository.Update(type, applicationDbContext);
        }
    }
}