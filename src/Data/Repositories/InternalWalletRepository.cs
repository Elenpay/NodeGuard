using FundsManager.Data.Models;
using FundsManager.Data.Repositories.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace FundsManager.Data.Repositories
{
    public class InternalWalletRepository : IInternalWalletRepository
    {
        private readonly IRepository<InternalWallet> _repository;
        private readonly ILogger<InternalWalletRepository> _logger;
        private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;

        public InternalWalletRepository(IRepository<InternalWallet> repository,
            ILogger<InternalWalletRepository> logger,
            IDbContextFactory<ApplicationDbContext> dbContextFactory)
        {
            _repository = repository;
            _logger = logger;
            _dbContextFactory = dbContextFactory;
        }

        public async Task<InternalWallet?> GetById(int id)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await applicationDbContext.InternalWallets.FirstOrDefaultAsync(x => x.Id == id);
        }

        public async Task<List<InternalWallet>> GetAll()
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await applicationDbContext.InternalWallets.OrderBy(x => x.Id).ToListAsync();
        }

        public async Task<(bool, string?)> AddAsync(InternalWallet type)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await _repository.AddAsync(type, applicationDbContext);
        }

        public async Task<(bool, string?)> AddRangeAsync(List<InternalWallet> type)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await _repository.AddRangeAsync(type, applicationDbContext);
        }

        public (bool, string?) Remove(InternalWallet type)
        {
            using var applicationDbContext = _dbContextFactory.CreateDbContext();

            return _repository.Remove(type, applicationDbContext);
        }

        public (bool, string?) RemoveRange(List<InternalWallet> types)
        {
            using var applicationDbContext = _dbContextFactory.CreateDbContext();

            return _repository.RemoveRange(types, applicationDbContext);
        }

        public (bool, string?) Update(InternalWallet type)
        {
            using var applicationDbContext = _dbContextFactory.CreateDbContext();

            return _repository.Update(type, applicationDbContext);
        }
    }
}