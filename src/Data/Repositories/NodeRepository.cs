using AutoMapper;
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
        private readonly IMapper _mapper; 

        public NodeRepository(IRepository<Node> repository,
            ILogger<NodeRepository> logger,
            IDbContextFactory<ApplicationDbContext> dbContextFactory,
            IMapper mapper)
        {
            _repository = repository;
            _logger = logger;
            _dbContextFactory = dbContextFactory;
            this._mapper = mapper;
        }

        public async Task<Node?> GetById(int id)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await applicationDbContext.Nodes
                .Include(node => node.Users)
                .ThenInclude(user => user.Keys)
                .ThenInclude(key => key.Wallets)
                .Include(x => x.ReturningFundsMultisigWallet)
                .ThenInclude(x => x.Keys)
                .SingleOrDefaultAsync(x => x.Id == id);
        }

        public async Task<Node?> GetByPubkey(string key)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await applicationDbContext.Nodes
                .Include(node => node.Users)
                .ThenInclude(user => user.Keys)
                .ThenInclude(keyObj => keyObj.Wallets)
                .Include(x => x.ReturningFundsMultisigWallet)
                .SingleOrDefaultAsync(x => x.PubKey == key);
        }

        public async Task<List<Node>> GetAll()
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await applicationDbContext.Nodes
                .Include(node => node.Users)
                .Include(node => node.ChannelOperationRequestsAsDestination)
                    .ThenInclude(request => request.Channel)
                .Include(x=> x.ReturningFundsMultisigWallet)
                .ToListAsync();
        }

        public async Task<List<Node>> GetAllManagedByFundsManager()
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            var resultAsync = await applicationDbContext.Nodes
                .Include(x => x.ReturningFundsMultisigWallet)
                .ThenInclude(x => x.Keys)
                .Where(node => node.Endpoint != null)
                .ToListAsync();

            return resultAsync;
        }

        public async Task<List<Node>> GetAllManagedByUser(string userId)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await applicationDbContext.Nodes
                .Where(node => node.Endpoint != null
                               && node.Users.Any(user => user.Id == userId))
                .ToListAsync();
        }

        public async Task<(bool, string?)> AddAsync(Node type)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            type.SetCreationDatetime();

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
            type.SetUpdateDatetime();
            
            type.Users?.Clear();
            type.ChannelOperationRequestsAsSource?.Clear();
            type.ChannelOperationRequestsAsDestination?.Clear();

            type = _mapper.Map<Node, Node>(type);
            
            return _repository.Update(type, applicationDbContext);
        }
    }
}