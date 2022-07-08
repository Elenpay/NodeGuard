using FundsManager.Data.Models;

namespace FundsManager.Data.Repositories.Interfaces;

public interface IInternalWalletRepository
{
    Task<InternalWallet?> GetById(int id);

    Task<List<InternalWallet>> GetAll();

    Task<(bool, string?)> AddAsync(InternalWallet type);

    Task<(bool, string?)> AddRangeAsync(List<InternalWallet> type);

    (bool, string?) Remove(InternalWallet type);

    (bool, string?) RemoveRange(List<InternalWallet> types);

    (bool, string?) Update(InternalWallet type);
}