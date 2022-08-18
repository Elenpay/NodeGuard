using AutoMapper;
using FundsManager.Data.Models;
using FundsManager.Data.Repositories.Interfaces;
using Humanizer;
using Microsoft.EntityFrameworkCore;

namespace FundsManager.Data.Repositories
{
    public class ChannelOperationRequestRepository : IChannelOperationRequestRepository
    {
        private readonly IRepository<ChannelOperationRequest> _repository;
        private readonly ILogger<ChannelOperationRequestRepository> _logger;
        private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;
        private readonly IMapper _mapper;

        public ChannelOperationRequestRepository(IRepository<ChannelOperationRequest> repository,
            ILogger<ChannelOperationRequestRepository> logger,
            IDbContextFactory<ApplicationDbContext> dbContextFactory, IMapper mapper)
        {
            _repository = repository;
            _logger = logger;
            _dbContextFactory = dbContextFactory;
            _mapper = mapper;
        }

        public async Task<ChannelOperationRequest?> GetById(int id)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            var request = await applicationDbContext.ChannelOperationRequests.Include(x => x.SourceNode)
                .Include(x => x.DestNode)
                .Include(x => x.Wallet).ThenInclude(x => x.InternalWallet)
                .Include(x => x.Wallet).ThenInclude(x => x.Keys)
                .Include(x => x.ChannelOperationRequestPsbts)
                .Include(x => x.Utxos)
                .FirstOrDefaultAsync(x => x.Id == id);

            return request;
        }

        public async Task<List<ChannelOperationRequest>> GetAll()
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await applicationDbContext.ChannelOperationRequests.ToListAsync();
        }

        public async Task<List<ChannelOperationRequest>> GetPendingRequestsByUser(string userId)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await applicationDbContext.ChannelOperationRequests
                .Where(request => request.Wallet.Keys.Any(key => key.User != null && key.User.Id == userId))
                .Include(request => request.Wallet)
                .Include(request => request.SourceNode)
                .Include(request => request.DestNode)
                .Include(request => request.ChannelOperationRequestPsbts)
                .Include(x => x.Utxos)
                .AsSplitQuery()
                .ToListAsync();
        }

        public async Task<List<ChannelOperationRequest>> GetUnsignedPendingRequestsByUser(string userId)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await applicationDbContext.ChannelOperationRequests
                .Where(request => request.Wallet.Keys.Any(key => key.User != null && key.User.Id == userId) &&
                request.ChannelOperationRequestPsbts.All(signature => signature.UserSignerId != userId))
                .Include(request => request.SourceNode)
                .Include(request => request.Wallet).ThenInclude(x => x.InternalWallet)
                .Include(x => x.Wallet).ThenInclude(x => x.Keys)
                .Include(request => request.DestNode)
                .Include(request => request.ChannelOperationRequestPsbts)
                .Include(x => x.Utxos)
                .AsSplitQuery()
                .ToListAsync();
        }

        public async Task<(bool, string?)> AddAsync(ChannelOperationRequest type)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            //We add a empty signature which will be the placeholder of the internal wallet key

            var valueTuple = await _repository.AddAsync(type, applicationDbContext);

            return valueTuple;
        }

        public async Task<(bool, string?)> AddRangeAsync(List<ChannelOperationRequest> type)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await _repository.AddRangeAsync(type, applicationDbContext);
        }

        public (bool, string?) Remove(ChannelOperationRequest type)
        {
            using var applicationDbContext = _dbContextFactory.CreateDbContext();

            return _repository.Remove(type, applicationDbContext);
        }

        public (bool, string?) RemoveRange(List<ChannelOperationRequest> types)
        {
            using var applicationDbContext = _dbContextFactory.CreateDbContext();

            return _repository.RemoveRange(types, applicationDbContext);
        }

        public (bool, string?) Update(ChannelOperationRequest type)
        {
            using var applicationDbContext = _dbContextFactory.CreateDbContext();

            //Automapper to remove collections
            var strippedType = _mapper.Map<ChannelOperationRequest, ChannelOperationRequest>(type);

            return _repository.Update(strippedType, applicationDbContext);
        }

        public async Task<(bool, string?)> AddUTXOs(ChannelOperationRequest type, List<FMUTXO> utxos)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            if (utxos.Count == 0) throw new ArgumentException("Value cannot be an empty collection.", nameof(utxos));

            (bool, string?) result = (true, null);

            try
            {
                await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

                var request = await applicationDbContext.ChannelOperationRequests.Include(x => x.Utxos)
                    .SingleOrDefaultAsync(x => x.Id == type.Id);
                if (request != null)
                {
                    if (!request.Utxos.Any())
                    {
                        request.Utxos = utxos;
                    }
                    else
                    {
                        request.Utxos.AddRange(utxos.Except(request.Utxos));
                    }

                    applicationDbContext.Update(request);

                    await applicationDbContext.SaveChangesAsync();
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error while adding UTXOs ({}) to op request:{}", utxos.Humanize(), type.Id);

                result.Item1 = false;
            }

            return result;
        }

        public async Task<List<ChannelOperationRequest>> GetPendingRequests()
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await applicationDbContext.ChannelOperationRequests
                .Where(request => request.Status == ChannelOperationRequestStatus.OnChainConfirmationPending
                                  || request.Status == ChannelOperationRequestStatus.Pending
                                  || request.Status == ChannelOperationRequestStatus.PSBTSignaturesPending)
                .Include(request => request.Wallet)
                .Include(request => request.SourceNode)
                .Include(request => request.DestNode)
                .Include(request => request.ChannelOperationRequestPsbts)
                .Include(x => x.Utxos).AsSplitQuery()
                .ToListAsync();
        }
    }
}