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

using System.ComponentModel.DataAnnotations;
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
   [Display(Name = "40swap")]
   FortySwap,
   /// <summary>
   /// NodeGuard's own Spark wallet: the node pays it over Lightning and it exits on-chain
   /// </summary>
   Spark = 2,
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

   /// <summary>The service fee in parts per million of the swap amount; null while it is not known</summary>
   [NotMapped]
   public long? ServiceFeePpm => FeePpm(ServiceFeeSats, SatsAmount);

   /// <summary>The Lightning routing fee in parts per million of the swap amount; null while it is not known</summary>
   [NotMapped]
   public long? LightningFeePpm => FeePpm(LightningFeeSats, SatsAmount);

   /// <summary>The on-chain fee in parts per million of the swap amount; null while it is not known</summary>
   [NotMapped]
   public long? OnChainFeePpm => FeePpm(OnChainFeeSats, SatsAmount);

   /// <summary>All fees in parts per million of the swap amount; null while none is known</summary>
   [NotMapped]
   public long? TotalFeesPpm => ServiceFeeSats is null && LightningFeeSats is null && OnChainFeeSats is null
      ? null
      : FeePpm(TotalFeesSats, SatsAmount);

   /// <summary><paramref name="feeSats"/> in parts per million of <paramref name="amountSats"/>, rounded down</summary>
   public static long? FeePpm(long? feeSats, long amountSats) =>
      feeSats is { } fee && amountSats > 0 ? fee * 1_000_000 / amountSats : null;

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

   /// <summary>
   /// The on-chain address of the destination wallet the swap pays out to. Loop and 40swap swaps reserve it
   /// when they are created, as their request carries it; a Spark swap reserves it only when it exits, and it
   /// is null until then. Null too for swaps created before it was recorded.
   /// </summary>
   public string? DestinationAddress { get; set; }

   /// <summary>
   /// Hex payment hash of the Lightning payment that funds the swap, when the provider exposes it
   /// (Loop's swap hash). Lets the payment be looked up on the node.
   /// </summary>
   public string? PaymentHash { get; set; }

   /// <summary>
   /// Spark swaps only: the identity public key of the Spark wallet that received the payment, which is the
   /// only wallet that can complete the swap
   /// </summary>
   public string? SparkIdentity { get; set; }

   /// <summary>
   /// Spark swaps only: the inbound Spark transfer that brought the swap's Lightning payment in. A transfer
   /// is given to one swap only
   /// </summary>
   public string? SparkTransferId { get; set; }

   /// <summary>
   /// Spark swaps only: the leaves that transfer brought in, comma-separated. They belong to this swap, and
   /// its exit moves exactly them to <see cref="DestinationAddress"/>
   /// </summary>
   public string? SparkLeafIds { get; set; }

   /// <summary>
   /// Spark swaps only: what those leaves add up to, less than <see cref="SatsAmount"/> when the SSP kept a
   /// receive fee
   /// </summary>
   public long? SparkReceivedSats { get; set; }

   /// <summary>
   /// What the swap paid on-chain to <see cref="DestinationAddress"/>, once confirmed: what it delivered.
   /// Recorded for Spark swaps
   /// </summary>
   public long? PayoutSats { get; set; }

}