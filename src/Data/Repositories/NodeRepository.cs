using FundsManager.Data.Models;
using FundsManager.Data.Repositories.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace FundsManager.Data.Repositories
{
    public class NodeRepository : INodeRepository
    {
        private readonly IRepository<Node> _repository;
        private readonly ILogger<NodeRepository> _logger;
        private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;

        public NodeRepository(IRepository<Node> repository,
            ILogger<NodeRepository> logger,
            IDbContextFactory<ApplicationDbContext> dbContextFactory)
        {
            _repository = repository;
            _logger = logger;
            _dbContextFactory = dbContextFactory;
        }

        public async Task<Node?> GetById(int id)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await applicationDbContext.Nodes
                .Include(node => node.Users)
                .ThenInclude(user => user.Keys)
                .ThenInclude(key => key.Wallets)
                .FirstOrDefaultAsync(x => x.Id == id);
        }

        public async Task<Node?> GetByPubkey(string key)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await applicationDbContext.Nodes
                .Include(node => node.Users)
                .ThenInclude(user => user.Keys)
                .ThenInclude(keyObj => keyObj.Wallets)
                .FirstOrDefaultAsync(x => x.PubKey == key);
        }

        public async Task<List<Node>> GetAll()
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await applicationDbContext.Nodes
                .Include(node => node.Users)
                .Include(node => node.ChannelOperationRequestsAsDestination)
                    .ThenInclude(request => request.Channel)
                .ToListAsync();
        }
        
        public async Task<List<Node>> GetAllManagedByUser(string userId)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await applicationDbContext.Nodes
                .Where(node => node.Endpoint != null && node.Users.Any(user => user.Id == userId))
                .ToListAsync();
        }

        public async Task<(bool, string?)> AddAsync(Node type)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await _repository.AddAsync(type, applicationDbContext);
        }

        public async Task<(bool, string?)> AddRangeAsync(List<Node> type)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await _repository.AddRangeAsync(type, applicationDbContext);
        }

        public (bool, string?) Remove(Node type)
        {
            using var applicationDbContext = _dbContextFactory.CreateDbContext();

            return _repository.Remove(type, applicationDbContext);
        }

        public (bool, string?) RemoveRange(List<Node> types)
        {
            using var applicationDbContext = _dbContextFactory.CreateDbContext();

            return _repository.RemoveRange(types, applicationDbContext);
        }

        public (bool, string?) Update(Node type)
        {
            using var applicationDbContext = _dbContextFactory.CreateDbContext();

            return _repository.Update(type, applicationDbContext);
        }
    }
}

