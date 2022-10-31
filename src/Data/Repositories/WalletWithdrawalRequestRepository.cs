using AutoMapper;
using FundsManager.Data.Models;
using FundsManager.Data.Repositories.Interfaces;
using FundsManager.Services;
using Humanizer;
using Microsoft.EntityFrameworkCore;

namespace FundsManager.Data.Repositories
{
    public class WalletWithdrawalRequestRepository : IWalletWithdrawalRequestRepository
    {
        private readonly IRepository<WalletWithdrawalRequest> _repository;
        private readonly ILogger<WalletWithdrawalRequestRepository> _logger;
        private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;
        private readonly IMapper _mapper;
        private readonly NotificationService _notificationService;

        public WalletWithdrawalRequestRepository(IRepository<WalletWithdrawalRequest> repository,
            ILogger<WalletWithdrawalRequestRepository> logger,
            IDbContextFactory<ApplicationDbContext> dbContextFactory, 
            IMapper mapper, 
            NotificationService notificationService)
        {
            _repository = repository;
            _logger = logger;
            _dbContextFactory = dbContextFactory;
            _mapper = mapper;
            _notificationService = notificationService;
        }

        public async Task<WalletWithdrawalRequest?> GetById(int id)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            var request = await applicationDbContext.WalletWithdrawalRequests
                .Include(x => x.Wallet)
                .ThenInclude(x => x.InternalWallet)
                .Include(x => x.Wallet)
                .ThenInclude(x => x.Keys)
                .Include(x => x.UserRequestor)
                .Include(x => x.WalletWithdrawalRequestPSBTs)
                .SingleOrDefaultAsync(x => x.Id == id);

            return request;
        }

        public async Task<List<WalletWithdrawalRequest>> GetAll()
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await applicationDbContext.WalletWithdrawalRequests
                .Include(x => x.Wallet).ThenInclude(x => x.Keys)
                .Include(x => x.UserRequestor)
                .Include(x => x.WalletWithdrawalRequestPSBTs)
                .AsSplitQuery()
                .ToListAsync();
        }

        public async Task<List<WalletWithdrawalRequest>> GetUnsignedPendingRequestsByUser(string userId)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await applicationDbContext.WalletWithdrawalRequests
                .Include(x => x.Wallet).ThenInclude(x => x.Keys)
                .Include(x => x.UserRequestor)
                .Include(x => x.WalletWithdrawalRequestPSBTs)
                .Where(request => request.Wallet.Keys.Any(key => key.User != null && key.User.Id == userId) &&
                                  (request.Status == WalletWithdrawalRequestStatus.Pending || request.Status == WalletWithdrawalRequestStatus.PSBTSignaturesPending) &&
                                  request.WalletWithdrawalRequestPSBTs.All(signature => signature.SignerId != userId))
                .AsSplitQuery()
                .ToListAsync();
        }

        public async Task<(bool, string?)> AddAsync(WalletWithdrawalRequest type)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            type.SetCreationDatetime();
            type.SetUpdateDatetime();

            var valueTuple = await _repository.AddAsync(type, applicationDbContext);
            await _notificationService.NotifyRequestSigners(type.WalletId, "/withdrawals");

            return valueTuple;
        }

        public async Task<(bool, string?)> AddRangeAsync(List<WalletWithdrawalRequest> type)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await _repository.AddRangeAsync(type, applicationDbContext);
        }

        public (bool, string?) Remove(WalletWithdrawalRequest type)
        {
            using var applicationDbContext = _dbContextFactory.CreateDbContext();

            return _repository.Remove(type, applicationDbContext);
        }

        public (bool, string?) RemoveRange(List<WalletWithdrawalRequest> types)
        {
            using var applicationDbContext = _dbContextFactory.CreateDbContext();

            return _repository.RemoveRange(types, applicationDbContext);
        }

        public (bool, string?) Update(WalletWithdrawalRequest type)
        {
            using var applicationDbContext = _dbContextFactory.CreateDbContext();

            //Automapper to remove collections
            var strippedType = _mapper.Map<WalletWithdrawalRequest, WalletWithdrawalRequest>(type);
            type.SetUpdateDatetime();

            return _repository.Update(strippedType, applicationDbContext);
        }

        public async Task<(bool, string?)> AddUTXOs(WalletWithdrawalRequest type, List<FMUTXO> utxos)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            if (utxos.Count == 0) throw new ArgumentException("Value cannot be an empty collection.", nameof(utxos));

            (bool, string?) result = (true, null);

            try
            {
                await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

                var request = await applicationDbContext.WalletWithdrawalRequests.Include(x => x.UTXOs)
                    .SingleOrDefaultAsync(x => x.Id == type.Id);
                if (request != null)
                {
                    if (!request.UTXOs.Any())
                    {
                        request.UTXOs = utxos;
                    }
                    else
                    {
                        request.UTXOs.AddRange(utxos.Except(request.UTXOs));
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

        public async Task<List<WalletWithdrawalRequest>> GetPendingRequests()
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            var walletWithdrawalRequests = await applicationDbContext.WalletWithdrawalRequests
                .Where(request => request.Status == WalletWithdrawalRequestStatus.OnChainConfirmationPending
                                  || request.Status == WalletWithdrawalRequestStatus.Pending
                                  || request.Status == WalletWithdrawalRequestStatus.PSBTSignaturesPending)
                .Include(request => request.Wallet)
                .ToListAsync();

            return walletWithdrawalRequests;
        }

        public async Task<List<WalletWithdrawalRequest>> GetOnChainPendingWithdrawals()
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            var walletWithdrawalRequests = await applicationDbContext.WalletWithdrawalRequests
                .Where(request => request.Status == WalletWithdrawalRequestStatus.OnChainConfirmationPending)
                .Include(request => request.Wallet)
                .ToListAsync();

            return walletWithdrawalRequests;
        }
    }
}