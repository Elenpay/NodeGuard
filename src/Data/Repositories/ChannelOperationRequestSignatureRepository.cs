using FundsManager.Data.Models;
using FundsManager.Data.Repositories.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace FundsManager.Data.Repositories
{
    public class ChannelOperationRequestSignatureRepository : IChannelOperationRequestSignatureRepository
    {
        private readonly IRepository<ChannelOperationRequestSignature> _repository;
        private readonly ILogger<ChannelOperationRequestSignatureRepository> _logger;
        private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;

        public ChannelOperationRequestSignatureRepository(IRepository<ChannelOperationRequestSignature> repository,
            ILogger<ChannelOperationRequestSignatureRepository> logger,
            IDbContextFactory<ApplicationDbContext> dbContextFactory)
        {
            _repository = repository;
            _logger = logger;
            _dbContextFactory = dbContextFactory;
        }

        public async Task<ChannelOperationRequestSignature?> GetById(int id)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await applicationDbContext.ChannelOperationRequestSignatures.FirstOrDefaultAsync(x => x.Id == id);
        }

        public async Task<List<ChannelOperationRequestSignature>> GetAll()
        {
            throw new NotImplementedException();
        }

        public async Task<(bool, string?)> AddAsync(ChannelOperationRequestSignature type)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await _repository.AddAsync(type, applicationDbContext);
        }

        public async Task<(bool, string?)> AddRangeAsync(List<ChannelOperationRequestSignature> type)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await _repository.AddRangeAsync(type, applicationDbContext);
        }

        public (bool, string?) Remove(ChannelOperationRequestSignature type)
        {
            using var applicationDbContext = _dbContextFactory.CreateDbContext();

            return _repository.Remove(type, applicationDbContext);
        }

        public (bool, string?) RemoveRange(List<ChannelOperationRequestSignature> types)
        {
            using var applicationDbContext = _dbContextFactory.CreateDbContext();

            return _repository.RemoveRange(types, applicationDbContext);
        }

        public (bool, string?) Update(ChannelOperationRequestSignature type)
        {
            using var applicationDbContext = _dbContextFactory.CreateDbContext();

            return _repository.Update(type, applicationDbContext);
        }
    }
}

