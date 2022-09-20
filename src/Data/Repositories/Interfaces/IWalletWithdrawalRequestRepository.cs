using FundsManager.Data.Models;

namespace FundsManager.Data.Repositories.Interfaces;

public interface IWalletWithdrawalRequestRepository
{
    Task<WalletWithdrawalRequest?> GetById(int id);

    Task<List<WalletWithdrawalRequest>> GetAll();

    Task<List<WalletWithdrawalRequest>> GetUnsignedPendingRequestsByUser(string userId);

    Task<(bool, string?)> AddAsync(WalletWithdrawalRequest type);

    Task<(bool, string?)> AddRangeAsync(List<WalletWithdrawalRequest> type);

    (bool, string?) Remove(WalletWithdrawalRequest type);

    (bool, string?) RemoveRange(List<WalletWithdrawalRequest> types);

    (bool, string?) Update(WalletWithdrawalRequest type);

    Task<(bool, string?)> AddUTXOs(WalletWithdrawalRequest type, List<FMUTXO> utxos);

    Task<List<WalletWithdrawalRequest>> GetPendingRequests();

    Task<List<WalletWithdrawalRequest>> GetOnChainPendingWithdrawals();
}