using FundsManager.Data.Models;

namespace FundsManager.Data.Repositories;

public interface ILiquidityRuleRepository
{
    Task<LiquidityRule?> GetById(int id);
    Task<List<LiquidityRule>> GetAll();
    Task<(bool, string?)> AddAsync(LiquidityRule type);
    Task<(bool, string?)> AddRangeAsync(List<LiquidityRule> type);
    (bool, string?) Remove(LiquidityRule type);
    (bool, string?) RemoveRange(List<LiquidityRule> types);
    (bool, string?) Update(LiquidityRule type);
    
    /// <summary>
    /// Gets all the liquidity rules for a given node
    /// </summary>
    /// <param name="nodePubKey"></param>
    /// <returns></returns>
    Task<ICollection<LiquidityRule>> GetByNodePubKey(string nodePubKey);
}