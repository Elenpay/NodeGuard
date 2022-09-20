using FundsManager.Data.Models;
using FundsManager.Data.Repositories.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace FundsManager.Data.Repositories
{
    public class FUTXORepository : IFMUTXORepository
    {
        private readonly IRepository<FMUTXO> _repository;
        private readonly ILogger<FUTXORepository> _logger;
        private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;

        public FUTXORepository(IRepository<FMUTXO> repository,
            ILogger<FUTXORepository> logger,
            IDbContextFactory<ApplicationDbContext> dbContextFactory)
        {
            _repository = repository;
            _logger = logger;
            _dbContextFactory = dbContextFactory;
        }

        public async Task<FMUTXO?> GetById(int id)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await applicationDbContext.FMUTXOs.FirstOrDefaultAsync(x => x.Id == id);
        }

        public async Task<List<FMUTXO>> GetAll()
        {
            throw new NotImplementedException();
        }

        public async Task<(bool, string?)> AddAsync(FMUTXO type)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            type.SetCreationDatetime();

            return await _repository.AddAsync(type, applicationDbContext);
        }

        public async Task<(bool, string?)> AddRangeAsync(List<FMUTXO> type)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await _repository.AddRangeAsync(type, applicationDbContext);
        }

        public (bool, string?) Remove(FMUTXO type)
        {
            using var applicationDbContext = _dbContextFactory.CreateDbContext();

            return _repository.Remove(type, applicationDbContext);
        }

        public (bool, string?) RemoveRange(List<FMUTXO> types)
        {
            using var applicationDbContext = _dbContextFactory.CreateDbContext();

            return _repository.RemoveRange(types, applicationDbContext);
        }

        public (bool, string?) Update(FMUTXO type)
        {
            using var applicationDbContext = _dbContextFactory.CreateDbContext();

            type.SetUpdateDatetime();

            return _repository.Update(type, applicationDbContext);
        }

        public async Task<List<FMUTXO>> GetLockedUTXOs(int? ignoredWalletWithdrawalRequestId = null, int? ignoredChannelOperationRequestId = null)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            var walletWithdrawalRequestsLockedUTXOs = new List<FMUTXO>();
            if (ignoredWalletWithdrawalRequestId == null)
            {
                walletWithdrawalRequestsLockedUTXOs = await applicationDbContext.WalletWithdrawalRequests
                    .Include(x => x.UTXOs)
                    .Where(x => x.Status == WalletWithdrawalRequestStatus.Pending ||
                                x.Status == WalletWithdrawalRequestStatus.OnChainConfirmationPending)
                    .SelectMany(x => x.UTXOs).ToListAsync();
            }
            else
            {
                walletWithdrawalRequestsLockedUTXOs = await applicationDbContext.WalletWithdrawalRequests
                    .Include(x => x.UTXOs)
                    .Where(x => x.Id != ignoredWalletWithdrawalRequestId
                                && x.Status == WalletWithdrawalRequestStatus.Pending ||
                                x.Status == WalletWithdrawalRequestStatus.OnChainConfirmationPending)
                    .SelectMany(x => x.UTXOs).ToListAsync();
            }

            var channelOperationRequestsLockedUTXOs = new List<FMUTXO>();

            if (ignoredChannelOperationRequestId == null)
            {
                channelOperationRequestsLockedUTXOs = await applicationDbContext.ChannelOperationRequests.Include(x => x.Utxos)
                    .Where(x => x.Status == ChannelOperationRequestStatus.Pending ||
                                x.Status == ChannelOperationRequestStatus.OnChainConfirmationPending)
                    .SelectMany(x => x.Utxos).ToListAsync();
            }
            else
            {
                channelOperationRequestsLockedUTXOs = await applicationDbContext.ChannelOperationRequests.Include(x => x.Utxos)
                    .Where(x => x.Id != ignoredChannelOperationRequestId
                        && x.Status == ChannelOperationRequestStatus.Pending ||
                                x.Status == ChannelOperationRequestStatus.OnChainConfirmationPending)
                    .SelectMany(x => x.Utxos).ToListAsync();
            }

            var result = walletWithdrawalRequestsLockedUTXOs.Union(channelOperationRequestsLockedUTXOs).ToList();

            return result;
        }
    }
}