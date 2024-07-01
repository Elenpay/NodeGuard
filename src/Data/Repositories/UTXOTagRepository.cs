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
    
    public async Task<List<UTXOTag>> GetByOutpoint(string outpoint)
    {
        await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();
        
        return await applicationDbContext.UTXOTags.Where(x => x.Outpoint == outpoint).ToListAsync();
    }

    public async Task<UTXOTag?> GetByKeyAndOutpoint(string key, string outpoint)
    {
        await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();
        
        return await applicationDbContext.UTXOTags.FirstOrDefaultAsync(x => x.Key == key && x.Outpoint == outpoint);
    }

    public async Task<List<UTXOTag>> GetByKeyValue(string key, string value)
    {
        await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();
        
        return await applicationDbContext.UTXOTags
            .Where(x => x.Key == key && x.Value == value).ToListAsync();
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

        foreach (var t in type)
        {
            t.SetCreationDatetime();
        }

        return await _repository.AddRangeAsync(type, applicationDbContext);
    }

    public async Task<(bool, string?)> UpsertRangeAsync(List<UTXOTag> entities)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();

        // Load existing entities based on composite keys in a single query
        var tags = entities.Select(e => e.Key).ToList();
        var utxoIds = entities.Select(e => e.Outpoint).ToList();

        var existingEntities = await dbContext.UTXOTags
            .Where(e => tags.Contains(e.Key) && utxoIds.Contains(e.Outpoint))
            .ToListAsync();

        foreach (var entity in entities)
        {
            var existingEntity = existingEntities
                .FirstOrDefault(e => e.Key == entity.Key && e.Outpoint == entity.Outpoint);

            if (existingEntity == null)
            {
                // New entity, set CreatedAt
                entity.SetCreationDatetime();
                dbContext.UTXOTags.Add(entity);
            }
            else
            {
                // Existing entity, set UpdatedAt and update properties
                existingEntity.SetUpdateDatetime();
                existingEntity.Key = entity.Key;
                existingEntity.Value = entity.Value;
                existingEntity.Outpoint = entity.Outpoint;
                dbContext.Entry(existingEntity).State = EntityState.Modified;
            }
        }

        var result = await dbContext.SaveChangesAsync();
        return result > 0 ? (true, null) : (false, "Failed to save changes");
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
