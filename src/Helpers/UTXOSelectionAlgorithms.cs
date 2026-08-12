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

using NodeGuard.Data.Models;
using NBitcoin;
using NBXplorer.Models;

namespace NodeGuard.Helpers
{
    /// <summary>
    /// Coin selection algorithms used to pick which UTXOs of a wallet fund a request (Withdrawals,
    /// ChannelOperationRequest). These are domain-agnostic on-chain algorithms, the caller decides
    /// which one applies.
    /// </summary>
    public static class UTXOSelectionAlgorithms
    {
        /// <summary>
        /// Validations shared by every selection algorithm. Throws on invalid arguments, and returns false
        /// when the available UTXOs cannot fund the request, in which case the algorithm has nothing to
        /// select and must return an empty selection.
        /// </summary>
        /// <param name="wallet"></param>
        /// <param name="satsAmount"></param>
        /// <param name="availableUTXOs"></param>
        /// <param name="logger"></param>
        /// <returns>Whether a selection can be made at all</returns>
        private static bool ValidateArguments(
            Wallet wallet, long satsAmount, List<UTXO> availableUTXOs, ILogger logger)
        {
            ArgumentNullException.ThrowIfNull(wallet);
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(satsAmount);

            if (!availableUTXOs.Any())
            {
                logger.LogError("The PSBT cannot be generated, no UTXOs are available for walletId: {WalletId}",
                    wallet.Id);
                return false;
            }

            var totalUTXOsConfirmedSats = availableUTXOs.Sum(x => ((Money)x.Value).Satoshi);

            if (totalUTXOsConfirmedSats < satsAmount)
            {
                logger.LogError(
                    "Error, the total UTXOs set balance for walletid: {WalletId} ({AvailableSats} sats) is less than the amount in the request ({RequestedSats} sats)",
                    wallet.Id, totalUTXOsConfirmedSats, satsAmount);
                return false;
            }

            return true;
        }

        /// <summary>
        /// Selects utxos from a wallet for requests (Withdrawals, ChannelOperationRequest) by oldest
        /// </summary>
        /// <param name="wallet"></param>
        /// <param name="satsAmount"></param>
        /// <param name="availableUTXOs"></param>
        /// <param name="logger"></param>
        /// <returns></returns>
        public static List<UTXO> SelectUTXOsByOldest(
            Wallet wallet, long satsAmount, List<UTXO> availableUTXOs, ILogger logger)
        {
            var selectedUTXOs = new List<UTXO>();

            if (!ValidateArguments(wallet, satsAmount, availableUTXOs, logger))
            {
                return selectedUTXOs;
            }

            var utxosStack = new Stack<UTXO>(availableUTXOs.OrderByDescending(x => x.Confirmations));

            //FIFO Algorithm to match the amount, oldest UTXOs are first taken

            var utxosSatsAmountAccumulator = 0M;

            var iterations = 0;
            while (satsAmount >= utxosSatsAmountAccumulator)
            {
                if (utxosStack.TryPop(out var utxo))
                {
                    selectedUTXOs.Add(utxo);
                    utxosSatsAmountAccumulator += ((Money)utxo.Value).Satoshi;
                }

                iterations++;

                if (iterations == 1_000)
                {
                    break;
                }
            }

            return selectedUTXOs;
        }

        /// <summary>
        /// Selects utxos from a wallet for requests (Withdrawals, ChannelOperationRequest) by closest, i.e.
        /// the UTXOs whose amount differs the least from the requested one are taken first. A single UTXO
        /// covering the whole amount is therefore preferred over several smaller ones, as long as it is the
        /// closest to the amount and holds more than it, so that the transaction has something to pay its
        /// miner fee with.
        /// </summary>
        /// <param name="wallet"></param>
        /// <param name="satsAmount"></param>
        /// <param name="availableUTXOs"></param>
        /// <param name="logger"></param>
        /// <returns></returns>
        public static List<UTXO> SelectUTXOsByClosest(
            Wallet wallet, long satsAmount, List<UTXO> availableUTXOs, ILogger logger)
        {
            var selectedUTXOs = new List<UTXO>();

            if (!ValidateArguments(wallet, satsAmount, availableUTXOs, logger))
            {
                return selectedUTXOs;
            }

            //Closest-first: the queue is ordered once by how far each UTXO is from the requested amount
            var utxosQueue = new Queue<UTXO>(
                availableUTXOs.OrderBy(x => Math.Abs(((Money)x.Value).Satoshi - satsAmount)));

            //Take UTXOs off the queue until the total is strictly over the amount: the miner fee is paid out of
            //whatever the inputs hold above it, so matching the amount exactly leaves nothing to pay it with
            var remainingSats = satsAmount;
            while (remainingSats >= 0 && utxosQueue.TryDequeue(out var utxo))
            {
                selectedUTXOs.Add(utxo);
                remainingSats -= ((Money)utxo.Value).Satoshi;
            }

            return selectedUTXOs;
        }
    }
}
