using Microsoft.EntityFrameworkCore;
using NodeGuard.Data.Models;
using NodeGuard.Data.Repositories.Interfaces;

namespace NodeGuard.Data.Repositories;

public class UTXOTagRepository : IUTXOTagRepository
{
    private readonly IRepository<UTXOTag> _repository;
    private readonly ILogger<UTXOTagRepository> _logger;
    private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;
    
    public UTXOTagRepository(IRepository<UTXOTag> repository,
        ILogger<UTXOTagRepository> logger,
        IDbContextFactory<ApplicationDbContext> dbContextFactory)
    {
        _repository = repository;
        _logger = logger;
        _dbContextFactory = dbContextFactory;
    }
    
    public async Task<UTXOTag?> GetByOutpoint(string outpoint)
    {
        await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();
        
        return await applicationDbContext.UTXOTags.FirstOrDefaultAsync(x => x.Outpoint == outpoint);
    }

    public async Task<UTXOTag?> GetTagByKeyAndOutpoint(string key, string outpoint)
    {
        await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();
        
return await applicationDbContext.UTXOTags.FirstOrDefaultAsync(x => x.Key == key && x.Outpoint == outpoint);
    }

    public async Task<(bool, string?)> AddAsync(UTXOTag type)
    {
        await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();
        
        type.SetCreationDatetime();
        
        return await _repository.AddAsync(type, applicationDbContext);
    }

    public async Task<(bool, string?)> AddRangeAsync(List<UTXOTag> type)
    {
        await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();
        
        return await _repository.AddRangeAsync(type, applicationDbContext);
    }

    public (bool, string?) Remove(UTXOTag type)
    {
        using var applicationDbContext = _dbContextFactory.CreateDbContext();
        
        return _repository.Remove(type, applicationDbContext);
    }

    public (bool, string?) RemoveRange(List<UTXOTag> types)
    {
        using var applicationDbContext = _dbContextFactory.CreateDbContext();
        
        return _repository.RemoveRange(types, applicationDbContext);
    }

    public (bool, string?) Update(UTXOTag type)
    {
        using var applicationDbContext = _dbContextFactory.CreateDbContext();

        type.SetUpdateDatetime();
        
        return _repository.Update(type, applicationDbContext);
    }
}