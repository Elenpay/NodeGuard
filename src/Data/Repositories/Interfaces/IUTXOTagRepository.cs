using NodeGuard.Data.Models;

namespace NodeGuard.Data.Repositories.Interfaces;

public interface IUTXOTagRepository
{
    Task<List<UTXOTag>> GetByOutpoint(string outpoint);
    
    Task<UTXOTag?> GetByKeyAndOutpoint(string key, string outpoint);
    
    Task<List<UTXOTag>> GetByKeyValue(string key, string value);
    
    Task<(bool, string?)> AddAsync(UTXOTag type);

    Task<(bool, string?)> AddRangeAsync(List<UTXOTag> type);

    (bool, string?) Remove(UTXOTag type);

    (bool, string?) RemoveRange(List<UTXOTag> types);

    (bool, string?) Update(UTXOTag type);

    Task<(bool, string?)> UpsertRangeAsync(List<UTXOTag> type);
}
