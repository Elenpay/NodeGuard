namespace FundsManager.Data.Models;

/// <summary>
/// A rule for setting liquidity automation on NodeGuard
/// </summary>
public class LiquidityRule: Entity
{
    
    public decimal? MinimumLocalBalance { get; set; }
    
    public decimal? MinimumRemoteBalance { get; set; }
    
    /// <summary>
    /// Target between 0 and 1 that we would like for the channel to be balanced after a rebalancing operation is complete
    /// </summary>
    public decimal? RebalanceTarget { get; set; }
    
    #region Relationships
    
    public int ChannelId { get; set; }
    public Channel Channel { get; set; }
  
    public int WalletId { get; set; }
    public Wallet Wallet { get; set; }
    
    public int NodeId { get; set; }
    public Node Node { get; set; }
    
    //TODO Discuss about a liquidity rule at node level

    #endregion
    
}
