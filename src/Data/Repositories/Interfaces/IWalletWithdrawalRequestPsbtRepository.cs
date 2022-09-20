using FundsManager.Data.Models;

namespace FundsManager.Data.Repositories.Interfaces;

public interface IWalletWithdrawalRequestPsbtRepository
{
    Task<WalletWithdrawalRequestPSBT?> GetById(int id);
    Task<List<WalletWithdrawalRequestPSBT>> GetAll();
    Task<(bool, string?)> AddAsync(WalletWithdrawalRequestPSBT type);
    Task<(bool, string?)> AddRangeAsync(List<WalletWithdrawalRequestPSBT> type);
    (bool, string?) Remove(WalletWithdrawalRequestPSBT type);
    (bool, string?) RemoveRange(List<WalletWithdrawalRequestPSBT> types);
    (bool, string?) Update(WalletWithdrawalRequestPSBT type);
}