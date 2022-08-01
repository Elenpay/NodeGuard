using FundsManager.Data.Models;

namespace FundsManager.Data.Repositories.Interfaces;

public interface IChannelRepository
{
    Task<Channel?> GetById(int id);
    Task<List<Channel>> GetAll();
    Task<(bool, string?)> AddAsync(Channel type);
    Task<(bool, string?)> AddRangeAsync(List<Channel> type);
    (bool, string?) Remove(Channel type);
    Task<(bool, string?)> SafeRemove(Channel type);
    (bool, string?) RemoveRange(List<Channel> types);
    (bool, string?) Update(Channel type);
}