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
            if (wallet == null) throw new ArgumentNullException(nameof(wallet));
            if (logger == null) throw new ArgumentNullException(nameof(logger));
            if (satsAmount <= 0) throw new ArgumentOutOfRangeException(nameof(satsAmount));

            var selectedUTXOs = new List<UTXO>();

            if (!availableUTXOs.Any())
            {
                logger.LogError("The PSBT cannot be generated, no UTXOs are available for walletId: {WalletId}",
                    wallet.Id);
                return selectedUTXOs;
            }

            var utxosStack = new Stack<UTXO>(availableUTXOs.OrderByDescending(x => x.Confirmations));

            //FIFO Algorithm to match the amount, oldest UTXOs are first taken

            var totalUTXOsConfirmedSats = utxosStack.Sum(x => ((Money)x.Value).Satoshi);

            if (totalUTXOsConfirmedSats < satsAmount)
            {
                logger.LogError(
                    "Error, the total UTXOs set balance for walletid: {WalletId} ({AvailableSats} sats) is less than the amount in the request ({RequestedSats} sats)",
                    wallet.Id, totalUTXOsConfirmedSats, satsAmount);
                return selectedUTXOs;
            }

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
    }
}
