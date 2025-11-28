/*
 * NodeGuard
 * Copyright (C) 2023  Elenpay
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program.  If not, see http://www.gnu.org/licenses/.
 *
 */

using System.ComponentModel.DataAnnotations.Schema;
using NBitcoin;

namespace NodeGuard.Data.Models;

public enum SwapDirection
{
   Out,
}
public enum SwapProvider
{
   Loop,
   FortySwap,
}

public enum SwapOutStatus
{
   Pending,
   Completed,
   Failed,
}


// TODO: When we support SwapOuts, we should rename this to just Swap and reuse this table for both
public class SwapOut : Entity
{
   public SwapProvider Provider { get; set; }

   /// <summary>
   /// The ID of the swap in the provider's system
   /// </summary>
   public string? ProviderId { get; set; }

   /// <summary>
   /// The current status of the swap
   /// </summary>
   public SwapOutStatus Status { get; set; }

   /// <summary>
   /// Whether the swap is manual or automatic
   /// </summary>
   public bool IsManual { get; set; }

   /// <summary>
   /// Amount in satoshis
   /// </summary>
   public long SatsAmount { get; set; }

   /// <summary>
   /// Calculated property to convert to btc
   /// </summary>
   [NotMapped]
   public decimal Amount => new Money(SatsAmount, MoneyUnit.Satoshi).ToDecimal(MoneyUnit.BTC);

   /// <summary>
   /// The address where the funds are sent to
   /// </summary>
   public int? DestinationWalletId { get; set; }

   public Wallet? DestinationWallet { get; set; }

   /// <summary>
   /// The node that executed the swap
   /// </summary>
   public int? NodeId { get; set; }
   public Node? Node { get; set; }

   /// <summary>
   /// Fees charged by the swap provider
   /// </summary>
   public long? ServiceFeeSats { get; set; }

   public Money ServiceFee => new Money(ServiceFeeSats ?? 0, MoneyUnit.Satoshi);

   /// <summary>
   /// Fees charged by the lightning network for the swap
   /// </summary>
   public long? LightningFeeSats { get; set; }

   public Money LightningFee => new Money(LightningFeeSats ?? 0, MoneyUnit.Satoshi);

   /// <summary>
   /// Fees charged by the on-chain network for the swap
   /// </summary>
   public long? OnChainFeeSats { get; set; }

   public Money OnChainFee => new Money(OnChainFeeSats ?? 0, MoneyUnit.Satoshi);

   public long TotalFeesSats =>
      (ServiceFeeSats ?? 0) + (LightningFeeSats ?? 0) + (OnChainFeeSats ?? 0);

   public Money TotalFees => new Money(TotalFeesSats, MoneyUnit.Satoshi);

   /// <summary>
   /// Error details if the swap failed
   /// </summary>
   public string? ErrorDetails { get; set; }

   public string? UserRequestorId { get; set; }

   public ApplicationUser? UserRequestor { get; set; }

   /// <summary>
   /// The Transaction ID of the swap
   /// </summary>
   public string? TxId { get; set; }

}