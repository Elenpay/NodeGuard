using FundsManager.Data.Models;

namespace FundsManager.Data.Repositories.Interfaces;

public interface IKeyRepository
{
    Task<Key?> GetById(int id);

    Task<List<Key>> GetAll();

    Task<(bool, string?)> AddAsync(Key type);

    Task<(bool, string?)> AddRangeAsync(List<Key> type);

    (bool, string?) Remove(Key type);

    (bool, string?) RemoveRange(List<Key> types);

    (bool, string?) Update(Key type);

    Task<List<Key>> GetUserKeys(ApplicationUser applicationUser);
}