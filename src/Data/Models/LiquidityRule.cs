namespace FundsManager.Data.Models;

/// <summary>
/// A rule for setting liquidity automation on NodeGuard
/// </summary>
public class LiquidityRule: Entity
{
    
    public decimal? MinimumLocalBalance { get; set; }
    
    public decimal? MinimumRemoteBalance { get; set; }
    
    //[Obsolete("Not implemented as loop does not support this")]
    /// <summary>
    /// Target between 0 and 1 that we would like for the channel to be balanced after a rebalancing operation is complete
    /// TODO: This is not currently used due to loop internal logic
    /// </summary>
    //public decimal? RebalanceTarget { get; set; }
    
    /// <summary>
    /// The direction that the liquidity will flow from the point of view of the local node
    /// </summary>
    /// TODO IF this might be needed (autoloop would need it)
    //public LiquidityRuleDirection LiquidityRuleDirection { get; set; }
    
    public bool IsEnabled { get; set; }
    
    
    #region Relationships
    
    public int ChannelId { get; set; }
    public Channel Channel { get; set; }
  
    public int WalletId { get; set; }
    public Wallet Wallet { get; set; }
    
    //TODO Discuss about a liquidity rule at node level

    #endregion
    
}

// public enum LiquidityRuleDirection
// { 
//     Out, //Reverse swap AKA Loop out L2->L1
//     In // Swap AKA Loop in L1->L2
// }