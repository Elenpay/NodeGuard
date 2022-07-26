using FundsManager.Data.Models;
using FundsManager.Data.Repositories.Interfaces;
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

            return _repository.Update(type, applicationDbContext);
        }
    }
}