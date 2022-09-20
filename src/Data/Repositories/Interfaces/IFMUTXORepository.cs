using FundsManager.Data.Models;

namespace FundsManager.Data.Repositories.Interfaces;

public interface IFMUTXORepository
{
    Task<FMUTXO?> GetById(int id);

    Task<List<FMUTXO>> GetAll();

    Task<(bool, string?)> AddAsync(FMUTXO type);

    Task<(bool, string?)> AddRangeAsync(List<FMUTXO> type);

    (bool, string?) Remove(FMUTXO type);

    (bool, string?) RemoveRange(List<FMUTXO> types);

    (bool, string?) Update(FMUTXO type);

    /// <summary>
    /// Gets the current list of UTXOs locked on requests ChannelOperationRequest / WalletWithdrawalRequest by passing its id if wants to remove it from the resulting set
    /// </summary>
    /// <returns></returns>
    Task<List<FMUTXO>> GetLockedUTXOs(int? ignoredWalletWithdrawalRequestId = null,
        int? ignoredChannelOperationRequestId = null);
}