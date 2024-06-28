using NodeGuard.Data.Models;

namespace NodeGuard.Data.Repositories.Interfaces;

public interface IUTXOTagRepository
{
    Task<UTXOTag?> GetByOutpoint(string outpoint);
    
    Task<(bool, string?)> AddAsync(UTXOTag type);

    Task<(bool, string?)> AddRangeAsync(List<UTXOTag> type);
    
    (bool, string?) Remove(UTXOTag type);

    (bool, string?) RemoveRange(List<UTXOTag> types);

    (bool, string?) Update(UTXOTag type);
}