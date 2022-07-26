using FundsManager.Data.Models;
using FundsManager.Data.Repositories.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace FundsManager.Data.Repositories
{
    public class ChannelRepository : IChannelRepository
    {
        private readonly IRepository<Channel> _repository;
        private readonly ILogger<ChannelRepository> _logger;
        private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;

        public ChannelRepository(IRepository<Channel> repository,
            ILogger<ChannelRepository> logger,
            IDbContextFactory<ApplicationDbContext> dbContextFactory)
        {
            _repository = repository;
            _logger = logger;
            _dbContextFactory = dbContextFactory;
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
                .Include(channel => channel.ChannelOperationRequests).ThenInclude(request => request.ChannelOperationRequestSignatures)
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

