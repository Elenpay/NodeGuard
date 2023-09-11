using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using NodeGuard.Data.Models;
using NodeGuard.Data.Repositories.Interfaces;
using NodeGuard.Services;

namespace NodeGuard.Data.Repositories;

public class APITokenRepository : IAPITokenRepository
{
    private readonly IRepository<APIToken> _repository;
    private readonly ISaltRepository _saltRepository;
    private readonly ILogger<APITokenRepository> _logger;
    private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;
    
    public APITokenRepository(IRepository<APIToken> repository,
        ISaltRepository saltRepository,
        ILogger<APITokenRepository> logger,
        IDbContextFactory<ApplicationDbContext> dbContextFactory)
    {
        _repository = repository;
        _saltRepository = saltRepository;
        _logger = logger;
        _dbContextFactory = dbContextFactory;
    }
    
    public async Task<(bool, string?)> AddAsync(APIToken type)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            type.SetCreationDatetime();
            type.SetUpdateDatetime();
            var saltresult = _saltRepository.Salt();
            type.GenerateTokenHash(saltresult.Item2);

            try
            {
                var addResult = await _repository.AddAsync(type, applicationDbContext);
                return addResult;
            }
            catch (Exception e)
            {
                const string errorWhileAddingOnRepository = "Error adding API token on repository";
                _logger.LogError(e, errorWhileAddingOnRepository);

                return (false, errorWhileAddingOnRepository);
            }
        }

    public async Task<List<APIToken>> GetAll()
    {
        await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();
        var result = await applicationDbContext.ApiTokens.ToListAsync();

        return result;
    }
    
    public (bool, string?) Update(APIToken type)
    {
        using var applicationDbContext = _dbContextFactory.CreateDbContext();
        
        type.SetUpdateDatetime();
        
        return _repository.Update(type, applicationDbContext);
    }
    
    public bool BlockToken(APIToken type)
    {
        try
        {
            type.IsBlocked = true;
            Update(type);
            return true;
        }
        catch (Exception e)
        {
            const string errorWhileBlockingToken = "Error while blocking token";
            _logger.LogError(e, errorWhileBlockingToken);
            return false;
        }
    }
}