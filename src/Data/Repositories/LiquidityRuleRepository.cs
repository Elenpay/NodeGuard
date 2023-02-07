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
 */

using FundsManager.Data.Models;
using FundsManager.Data.Repositories.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace FundsManager.Data.Repositories;

public class LiquidityRuleRepository : ILiquidityRuleRepository
{
    private readonly IRepository<LiquidityRule> _repository;
    private readonly ILogger<LiquidityRuleRepository> _logger;
    private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;

    public LiquidityRuleRepository(IRepository<LiquidityRule> repository,
        ILogger<LiquidityRuleRepository> logger,
        IDbContextFactory<ApplicationDbContext> dbContextFactory)
    {
        _repository = repository;
        _logger = logger;
        _dbContextFactory = dbContextFactory;
    }

    public async Task<LiquidityRule?> GetById(int id)
    {
        await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

        return await applicationDbContext.LiquidityRules.SingleOrDefaultAsync(x => x.Id == id);
    }

    public async Task<List<LiquidityRule>> GetAll()
    {
        await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

        return await applicationDbContext.LiquidityRules.ToListAsync();
    }

    public async Task<(bool, string?)> AddAsync(LiquidityRule type)
    {
        await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

        type.SetCreationDatetime();
        type.SetUpdateDatetime();

        return await _repository.AddAsync(type, applicationDbContext);
    }

    public async Task<(bool, string?)> AddRangeAsync(List<LiquidityRule> type)
    {
        await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

        return await _repository.AddRangeAsync(type, applicationDbContext);
    }

    public (bool, string?) Remove(LiquidityRule type)
    {
        using var applicationDbContext = _dbContextFactory.CreateDbContext();

        return _repository.Remove(type, applicationDbContext);
    }

    public (bool, string?) RemoveRange(List<LiquidityRule> types)
    {
        using var applicationDbContext = _dbContextFactory.CreateDbContext();

        return _repository.RemoveRange(types, applicationDbContext);
    }

    public (bool, string?) Update(LiquidityRule type)
    {
        using var applicationDbContext = _dbContextFactory.CreateDbContext();

        type.SetCreationDatetime();
        type.SetUpdateDatetime();


        return _repository.Update(type, applicationDbContext);
    }

    public async Task<ICollection<LiquidityRule>> GetByNodePubKey(string nodePubKey)
    {
        if (string.IsNullOrWhiteSpace(nodePubKey))
            throw new ArgumentException("Value cannot be null or whitespace.", nameof(nodePubKey));
        
        using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

        var result = applicationDbContext.LiquidityRules
            .Include(x=> x.Node)
            .Include(x=> x.Wallet)
            .ThenInclude(x=> x.InternalWallet)
            .Include(x=> x.Channel)
            .Where(x=> x.Node.PubKey == nodePubKey).ToList();
        
        return result;
    }
}