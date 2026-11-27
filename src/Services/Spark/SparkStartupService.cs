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

namespace NodeGuard.Services.Spark;

/// <summary>
/// Connects every Spark wallet in the background at startup, so a mismatched or unreachable signer, or a seed
/// that no longer decrypts, is reported (and Spark hidden for its nodes) before anyone tries a swap.
/// </summary>
public sealed class SparkStartupService(SparkSettings settings, ISparkWalletService sparkWallet, ILogger<SparkStartupService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        IReadOnlyList<SparkWalletEntry> wallets;
        try
        {
            wallets = await sparkWallet.GetWalletsAsync(stoppingToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Not fatal: each wallet still connects on first use
            logger.LogError(e, "Could not list the Spark wallets to connect them at startup");
            return;
        }

        if (settings is { IsMainnet: true, SignerMode: SparkSignerMode.Wallet } && wallets.Count > 0)
        {
            logger.LogWarning(
                "SPARK_SIGNER=wallet on mainnet: the keys of {Count} Spark wallets are hot, their seeds encrypted in NodeGuard's " +
                "database. Keep their balances transit-only, and move to the remote signer (SPARK_SIGNER=remote)", wallets.Count);
        }

        foreach (var wallet in wallets)
        {
            await sparkWallet.EnsureReadyAsync(wallet.Ref, stoppingToken);
        }
    }
}
