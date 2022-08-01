using FundsManager.Data.Models;

namespace FundsManager.Data.Repositories.Interfaces;

public interface IChannelOperationRequestPSBTRepository
{
    Task<ChannelOperationRequestPSBT?> GetById(int id);

    Task<List<ChannelOperationRequestPSBT>> GetAll();

    Task<(bool, string?)> AddAsync(ChannelOperationRequestPSBT type);

    Task<(bool, string?)> AddRangeAsync(List<ChannelOperationRequestPSBT> type);

    (bool, string?) Remove(ChannelOperationRequestPSBT type);

    (bool, string?) RemoveRange(List<ChannelOperationRequestPSBT> types);

    (bool, string?) Update(ChannelOperationRequestPSBT type);
}