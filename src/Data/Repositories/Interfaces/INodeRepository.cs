using FundsManager.Data.Models;

namespace FundsManager.Data.Repositories.Interfaces;

public interface INodeRepository
{
    Task<Node?> GetById(int id);
    Task<Node?> GetByPubkey(string key);
    Task<List<Node>> GetAll();
    Task<List<Node>> GetAllManaged();
    Task<(bool, string?)> AddAsync(Node type);
    Task<(bool, string?)> AddRangeAsync(List<Node> type);
    (bool, string?) Remove(Node type);
    (bool, string?) RemoveRange(List<Node> types);
    (bool, string?) Update(Node type);
}