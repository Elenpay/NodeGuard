using NodeGuard.Data.Models;

namespace NodeGuard.Data.Repositories.Interfaces;

public interface IAPITokenRepository
{
    Task<(bool, string?)> AddAsync(APIToken type);
    Task<APIToken?> GetByToken(string token, bool valid = false);
    Task<List<APIToken>> GetAll();
    (bool, string?) Update(APIToken type);
    bool BlockToken(APIToken type);
    bool UnblockToken(APIToken type);
    
}