using System.Security.Cryptography;
using Google.Protobuf.WellKnownTypes;
using Microsoft.EntityFrameworkCore;
using NodeGuard.Data.Models;
using NodeGuard.Data.Repositories.Interfaces;
using NodeGuard.Services;

namespace NodeGuard.Data.Repositories;

public class APITokenRepository : IAPITokenRepository
{
    private readonly IRepository<APIToken> _repository;
    private readonly ILogger<APITokenRepository> _logger;
    private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;
    
    public APITokenRepository(IRepository<APIToken> repository,
        ILogger<APITokenRepository> logger,
        IDbContextFactory<ApplicationDbContext> dbContextFactory)
    {
        _repository = repository;
        _logger = logger;
        _dbContextFactory = dbContextFactory;
    }
    
    public async Task<(bool, string?)> AddAsync(APIToken type)
        {
            await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync();

            type.SetCreationDatetime();
            type.SetUpdateDatetime();
            
            var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            try
            {
                type.GenerateTokenHash(password, Constants.API_TOKEN_SALT);
            }
            catch (Exception e)
            {
                var errorMsg = "Error generating API token hash";  
                _logger.LogError(e, errorMsg);

                return (false, errorMsg);
            }

            try
            {
                var addResult = await _repository.AddAsync(type, applicationDbContext);
                return addResult;
            }
            catch (Exception e)
            {
                var errorMsg = "Error adding API token on repository";
                _logger.LogError(e, errorMsg);

                return (false, errorMsg);
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
        return ChangeBlockStatus(type, true);
    }

    public bool UnblockToken(APIToken type)
    {
        return ChangeBlockStatus(type, false);
    }
    
    private bool ChangeBlockStatus(APIToken type, bool status)
    {
        try
        {
            type.IsBlocked = status;
            Update(type);
            return true;
        }
        catch (Exception e)
        {
            var errorWhileChangingBlockStatus = status ? "Error while blocking token" : "Error while unblocking token";
            _logger.LogError(e, errorWhileChangingBlockStatus);
            return false;
        }
    }
}