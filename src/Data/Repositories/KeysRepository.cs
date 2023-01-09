using FundsManager.Data.Models;
using FundsManager.Data.Repositories.Interfaces;
using FundsManager.Helpers;
using Microsoft.EntityFrameworkCore;
using NBitcoin;
using Key = FundsManager.Data.Models.Key;

namespace FundsManager.Data.Repositories
{
    public class KeyRepository : IKeyRepository
    {
        private readonly IRepository<Key> _repository;
        private readonly ILogger<KeyRepository> _logger;
        private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;
        private readonly IInternalWalletRepository _internalWalletRepository;

        public KeyRepository(IRepository<Key> repository,
            ILogger<KeyRepository> logger,
            IDbContextFactory<ApplicationDbContext> dbContextFactory,
            IInternalWalletRepository internalWalletRepository)
        {
            _repository = repository;
            _logger = logger;
            _dbContextFactory = dbContextFactory;
            _internalWalletRepository = internalWalletRepository;
        }

        public async Task<Key?> GetById(int id)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await applicationDbContext.Keys.FirstOrDefaultAsync(x => x.Id == id);
        }

        public async Task<List<Key>> GetAll()
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await applicationDbContext.Keys.ToListAsync();
        }

        public async Task<(bool, string?)> AddAsync(Key type)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            type.SetCreationDatetime();
            type.SetUpdateDatetime();

            try
            {
                var xpub = new BitcoinExtPubKey(type.XPUB, CurrentNetworkHelper.GetCurrentNetwork());
            }
            catch (Exception e)
            {
                const string errorWhileValidatingXpub = "Error while validating XPUB";

                _logger.LogError(errorWhileValidatingXpub);

                return (false, errorWhileValidatingXpub);
            }

            return await _repository.AddAsync(type, applicationDbContext);
        }

        public async Task<(bool, string?)> AddRangeAsync(List<Key> type)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await _repository.AddRangeAsync(type, applicationDbContext);
        }

        public (bool, string?) Remove(Key type)
        {
            using var applicationDbContext = _dbContextFactory.CreateDbContext();

            return _repository.Remove(type, applicationDbContext);
        }

        public (bool, string?) RemoveRange(List<Key> types)
        {
            using var applicationDbContext = _dbContextFactory.CreateDbContext();

            return _repository.RemoveRange(types, applicationDbContext);
        }

        public (bool, string?) Update(Key type)
        {
            using var applicationDbContext = _dbContextFactory.CreateDbContext();

            type.SetCreationDatetime();
            type.SetUpdateDatetime();

            type.Wallets = null;

            return _repository.Update(type, applicationDbContext);
        }

        public async Task<List<Key>> GetUserKeys(ApplicationUser applicationUser)
        {
            if (applicationUser == null) throw new ArgumentNullException(nameof(applicationUser));

            await using var applicationDbContext = _dbContextFactory.CreateDbContext();

            var result = await applicationDbContext.Keys.Include(x => x.Wallets).Where(x => x.UserId == applicationUser.Id).ToListAsync();

            return result;
        }

        public async Task<Key> GetCurrentInternalWalletKey()
        {
            await using var applicationDbContext = _dbContextFactory.CreateDbContext();

            var internalWallet = await _internalWalletRepository.GetCurrentInternalWallet();

            if (internalWallet == null)
                return null;

            var result = await applicationDbContext.Keys.OrderByDescending(x => x.Id).SingleOrDefaultAsync(x=> x.InternalWalletId == internalWallet.Id);
            
            //If they key does not exist we should create it
            if (result == null)
            {
                result = new Key
                {
                    CreationDatetime = DateTimeOffset.Now,
                    InternalWalletId = internalWallet.Id,
                    UpdateDatetime = DateTimeOffset.Now,
                    Name = "Internal wallet",
                    XPUB = internalWallet.XPUB,
                    MasterFingerprint = internalWallet.MasterFingerprint,
                    //Derivation path
                    Path = Environment.GetEnvironmentVariable("DEFAULT_DERIVATION_PATH") ?? throw new ArgumentException("DEFAULT_DERIVATION_PATH is not set"),

                };
                
            }

            return result;
        }
    }
}