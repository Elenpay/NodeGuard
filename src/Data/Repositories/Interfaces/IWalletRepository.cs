using FundsManager.Data.Models;

namespace FundsManager.Data.Repositories.Interfaces;

public interface IWalletRepository
{
    Task<Wallet?> GetById(int id);

    Task<List<Wallet>> GetAll();

    Task<(bool, string?)> AddAsync(Wallet type);

    Task<(bool, string?)> AddRangeAsync(List<Wallet> type);

    (bool, string?) Remove(Wallet type);

    (bool, string?) RemoveRange(List<Wallet> types);

    (bool, string?) Update(Wallet type);

    /// <summary>
    /// Enables the tracking of this wallet and locks the edition of its parameters other than name and description
    /// </summary>
    /// <param name="selectedWalletToFinalise"></param>
    /// <returns></returns>
    Task<(bool, string?)> FinaliseWallet(Wallet selectedWalletToFinalise);
}