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

using Grpc.Core;
using NBitcoin;
using NBitcoin.RPC;
using Nodeguard;

namespace NodeGuard.Tests.E2E;

/// <summary>
/// Shared helpers for E2E tests that share the "E2E" xunit collection (and therefore compete
/// for the same dev-seeded hot wallet across a single test run).
/// </summary>
internal static class E2EFundingHelper
{
    public static async Task MineAsync(RPCClient rpc, int blocks)
    {
        var addr = await rpc.GetNewAddressAsync();
        await rpc.GenerateToAddressAsync(blocks, addr);
    }

    /// <summary>
    /// Sends fresh funds to a reserved address of the wallet and mines them to confirmation, so a
    /// test does not depend on however much of the dev seed other E2E tests in this run left behind.
    /// </summary>
    public static async Task FundHotWalletAsync(
        NodeGuardService.NodeGuardServiceClient client, Metadata headers, RPCClient rpc, int walletId,
        Money amount, Action<string> log)
    {
        var addressResponse = await client.GetNewWalletAddressAsync(
            new GetNewWalletAddressRequest { WalletId = walletId, Skip = 0, Reserve = true }, headers);
        var address = BitcoinAddress.Create(addressResponse.Address, Network.RegTest);
        await rpc.SendToAddressAsync(address, amount);
        await MineAsync(rpc, 6);

        await RetryAsync(async () =>
        {
            var available = await client.GetAvailableUtxosAsync(
                new GetAvailableUtxosRequest { WalletId = walletId, Amount = amount.Satoshi }, headers);
            if (available.Confirmed.Sum(u => u.Amount) < amount.Satoshi)
                throw new InvalidOperationException("top-up UTXO not indexed/spendable yet");
            return true;
        }, attempts: 30, delay: TimeSpan.FromSeconds(4), what: "GetAvailableUtxos (top-up funded)", log: log);
    }

    public static async Task<T> RetryAsync<T>(
        Func<Task<T>> action, int attempts, TimeSpan delay, string what, Action<string> log)
    {
        Exception? last = null;
        for (var i = 0; i < attempts; i++)
        {
            try { return await action(); }
            catch (Exception ex) { last = ex; log($"{what} attempt {i + 1}/{attempts} failed: {ex.Message}"); }
            await Task.Delay(delay);
        }
        throw new InvalidOperationException($"{what} did not succeed after {attempts} attempts", last);
    }
}
