using FundsManager.Data.Models;

namespace FundsManager.Data.Repositories.Interfaces;

public interface IUTXORepository
{
    Task<FMUTXO?> GetById(int id);

    Task<List<FMUTXO>> GetAll();

    Task<(bool, string?)> AddAsync(FMUTXO type);

    Task<(bool, string?)> AddRangeAsync(List<FMUTXO> type);

    (bool, string?) Remove(FMUTXO type);

    (bool, string?) RemoveRange(List<FMUTXO> types);

    (bool, string?) Update(FMUTXO type);
}