using System.ComponentModel.DataAnnotations.Schema;

namespace NodeGuard.Data.Models;

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
    
    /// <summary>
    /// Let's you know if the rule has a wallet or an address as a target for the rebalancing operation
    /// </summary>
    public bool IsReverseSwapWalletRule { get; set; }
    
    /// <summary>
    /// In case that is a rule that sends the funds to an address instead of a wallet this is the address
    /// </summary>
    public string? ReverseSwapAddress { get; set; }
    
    #region Relationships
    
    public int ChannelId { get; set; }
    public Channel Channel { get; set; }
  
    public int SwapWalletId { get; set; }
    public Wallet SwapWallet { get; set; }
    
    public int? ReverseSwapWalletId { get; set; }
    public Wallet? ReverseSwapWallet { get; set; }
    
    public int NodeId { get; set; }
    public Node Node { get; set; }
    
    //TODO Discuss about a liquidity rule at node level

    #endregion
    
}
