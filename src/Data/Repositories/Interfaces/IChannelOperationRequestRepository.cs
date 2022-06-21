using FundsManager.Data.Models;

namespace FundsManager.Data.Repositories.Interfaces;

public interface IChannelOperationRequestRepository
{
    Task<ChannelOperationRequest?> GetById(int id);
    Task<List<ChannelOperationRequest>> GetAll();
    Task<(bool, string?)> AddAsync(ChannelOperationRequest type);
    Task<(bool, string?)> AddRangeAsync(List<ChannelOperationRequest> type);
    (bool, string?) Remove(ChannelOperationRequest type);
    (bool, string?) RemoveRange(List<ChannelOperationRequest> types);
    (bool, string?) Update(ChannelOperationRequest type);
}