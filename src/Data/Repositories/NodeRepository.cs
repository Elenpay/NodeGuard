/*
 * NodeGuard
 * Copyright (C) 2023  Elenpay
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program.  If not, see http://www.gnu.org/licenses/.
 *
 */

using AutoMapper;
using NodeGuard.Data.Models;
using NodeGuard.Data.Repositories.Interfaces;
using NodeGuard.Services;
using Microsoft.EntityFrameworkCore;

namespace NodeGuard.Data.Repositories
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
            _mapper = mapper;
        }

        public async Task<Node?> GetById(int id)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await applicationDbContext.Nodes
                .Include(node => node.Users)
                .ThenInclude(user => user.Keys)
                .ThenInclude(key => key.Wallets)
                .Include(x => x.ReturningFundsWallet)
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
                .Include(x => x.ReturningFundsWallet)
                .SingleOrDefaultAsync(x => x.PubKey == key);
        }
        

        public async Task<Node> GetOrCreateByPubKey(string pubKey, ILightningService lightningService)
        {
            var node = await GetByPubkey(pubKey);

            if (node == null)
            {
                var foundNode = await lightningService.GetNodeInfo(pubKey);
                if (foundNode == null)
                {
                    _logger.LogWarning("Peer with PubKey {pubKey} not found", pubKey);
                }

                node = new Node()
                {
                    Name = foundNode?.Alias ?? "",
                    PubKey = pubKey
                };
                var addNode = await AddAsync(node);
                if (!addNode.Item1)
                {
                    throw new Exception(addNode.Item2);
                }
            }

            return node;
        }

        public async Task<List<Node>> GetAll()
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await applicationDbContext.Nodes
                .Include(node => node.Users)
                .Include(x=> x.ChannelOperationRequestsAsSource)
                    .ThenInclude(request => request.Channel)
                .Include(node => node.ChannelOperationRequestsAsDestination)
                    .ThenInclude(request => request.Channel)
                .Include(x=> x.ReturningFundsWallet)
                .ToListAsync();
        }

        public async Task<List<Node>> GetAllManagedByNodeGuard()
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            var query = applicationDbContext.Nodes
                .Include(x => x.ReturningFundsWallet)
                .ThenInclude(x => x.Keys)
                .Include(x => x.ReturningFundsWallet)
                .Where(node => node.Endpoint != null);

            var resultAsync = await query.ToListAsync();

            return resultAsync;
        }
        
        public async Task<List<Node>> GetAllManagedByUser(string userId)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            return await applicationDbContext.Nodes
                .Include(x=> x.ReturningFundsWallet)
                .Include(x=> x.ChannelOperationRequestsAsDestination)
                .Include(x=> x.ChannelOperationRequestsAsSource)
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