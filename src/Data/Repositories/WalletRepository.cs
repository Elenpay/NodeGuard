using FundsManager.Data.Models;
using FundsManager.Data.Repositories.Interfaces;
using FundsManager.Services;
using Microsoft.EntityFrameworkCore;

namespace FundsManager.Data.Repositories
{
    public class WalletRepository : IWalletRepository
    {
        private readonly IRepository<Wallet> _repository;
        private readonly ILogger<WalletRepository> _logger;
        private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;
        private readonly IInternalWalletRepository _internalWalletRepository;
        private readonly IKeyRepository _keyRepository;

        public WalletRepository(IRepository<Wallet> repository,
            ILogger<WalletRepository> logger,
            IDbContextFactory<ApplicationDbContext> dbContextFactory,
            IInternalWalletRepository internalWalletRepository, IKeyRepository keyRepository)
        {
            _repository = repository;
            _logger = logger;
            _dbContextFactory = dbContextFactory;
            _internalWalletRepository = internalWalletRepository;
            _keyRepository = keyRepository;
        }

        public async Task<Wallet?> GetById(int id)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await applicationDbContext.Wallets.Include(x => x.InternalWallet)
                .Include(x => x.Keys)
                .FirstOrDefaultAsync(x => x.Id == id);
        }

        public async Task<List<Wallet>> GetAll()
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await applicationDbContext.Wallets.Include(x => x.InternalWallet).Include(x => x.Keys).ToListAsync();
        }

        public async Task<List<Wallet>> GetAvailableWallets()
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await applicationDbContext.Wallets
                .Where(wallet => !wallet.IsArchived && !wallet.IsCompromised && wallet.IsFinalised)
                .Include(x => x.InternalWallet)
                .Include(x => x.Keys)
                .ToListAsync();
        }
        
        public async Task<(bool, string?)> AddAsync(Wallet type)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            type.SetCreationDatetime();
            type.SetUpdateDatetime();

            //We add the internal wallet of the moment and its key
            var currentInternalWallet = (await _internalWalletRepository.GetCurrentInternalWallet());
            if (currentInternalWallet != null)
                type.InternalWalletId = currentInternalWallet.Id;

            var currentInternalWalletKey = await _keyRepository.GetCurrentInternalWalletKey();

            type.Keys = new List<Key>();

            var addResult = await _repository.AddAsync(type, applicationDbContext);

            if (currentInternalWalletKey != null)
                type.Keys.Add(currentInternalWalletKey);

            //We need to persist before updating a m-of-n relationship
            var updateResult = Update(type);

            //We tell nbxplorer to track this

            var (_, client) = LightningService.GenerateNetwork(_logger);

            try
            {
                await client.TrackAsync(type.GetDerivationStrategy());
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error while setting nbxplorer tracking on wallet:{}", type.Id);
                return (false, "Error while setting nbxplorer tracking on wallet");
            }

            return (addResult.Item1 && updateResult.Item1, addResult.Item2 + updateResult.Item2);
        }

        public async Task<(bool, string?)> AddRangeAsync(List<Wallet> type)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await _repository.AddRangeAsync(type, applicationDbContext);
        }

        public (bool, string?) Remove(Wallet type)
        {
            using var applicationDbContext = _dbContextFactory.CreateDbContext();

            return _repository.Remove(type, applicationDbContext);
        }

        public (bool, string?) RemoveRange(List<Wallet> types)
        {
            using var applicationDbContext = _dbContextFactory.CreateDbContext();

            return _repository.RemoveRange(types, applicationDbContext);
        }

        public (bool, string?) Update(Wallet type)
        {
            using var applicationDbContext = _dbContextFactory.CreateDbContext();
            type.SetUpdateDatetime();
            type.Keys?.Clear();
            type.ChannelOperationRequestsAsSource?.Clear();
            return _repository.Update(type, applicationDbContext);
        }

        public async Task<(bool, string?)> FinaliseWallet(Wallet selectedWalletToFinalise)
        {
            if (selectedWalletToFinalise == null) throw new ArgumentNullException(nameof(selectedWalletToFinalise));
            await using var applicationDbContext = _dbContextFactory.CreateDbContext();

            (bool, string?) result = (true, null);

            selectedWalletToFinalise.IsFinalised = true;
            try
            {
                var (_, nbxplorerClient) = LightningService.GenerateNetwork(_logger);

                await nbxplorerClient.TrackAsync(selectedWalletToFinalise.GetDerivationStrategy());

                selectedWalletToFinalise.Keys = null;
                selectedWalletToFinalise.ChannelOperationRequestsAsSource = null;

                var updateResult = Update(selectedWalletToFinalise);

                if (updateResult.Item1 == false)
                {
                    result = (false, "Error while finalising wallet");
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error while finalising wallet:{}", selectedWalletToFinalise.Id);

                result = (false, "Error while finalising wallet");
            }

            return result;
        }
    }
}