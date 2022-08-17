using FundsManager.Data.Models;

namespace FundsManager.Data.Repositories.Interfaces;

public interface IChannelOperationRequestRepository
{
    Task<ChannelOperationRequest?> GetById(int id);

    Task<List<ChannelOperationRequest>> GetAll();

    Task<List<ChannelOperationRequest>> GetPendingRequestsByUser(string userId);

    Task<List<ChannelOperationRequest>> GetUnsignedPendingRequestsByUser(string userId);

    Task<(bool, string?)> AddAsync(ChannelOperationRequest type);

    Task<(bool, string?)> AddRangeAsync(List<ChannelOperationRequest> type);

    (bool, string?) Remove(ChannelOperationRequest type);

    (bool, string?) RemoveRange(List<ChannelOperationRequest> types);

    (bool, string?) Update(ChannelOperationRequest type);

    /// <summary>
    /// Adds on the many-to-many collection the list of utxos provided
    /// </summary>
    /// <param name="type"></param>
    /// <param name="utxos"></param>
    /// <returns></returns>
    Task<(bool, string?)> AddUTXOs(ChannelOperationRequest type, List<FMUTXO> utxos);

    /// <summary>
    /// Returns those requests that can have a PSBT locked until they are confirmed / rejected / cancelled
    /// </summary>
    /// <returns></returns>
    Task<List<ChannelOperationRequest>> GetPendingRequests();
}