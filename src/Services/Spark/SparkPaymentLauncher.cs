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

namespace NodeGuard.Services.Spark;

/// <summary>A recorded Spark swap's Lightning payment, as its creation set it up.</summary>
/// <param name="TimeoutSeconds">How long LND keeps trying routes (the node's Spark payment timeout).</param>
public sealed record SparkPayment(Node Node, int SwapId, string PaymentRequest, long FeeLimitMsat, ulong[]? ChannelsOut,
    int TimeoutSeconds);

/// <summary>
/// Pays recorded Spark swaps in the background, so creating one doesn't wait for its payment: the swap shows as
/// pending until the payment's outcome is saved, here or by MonitorSwapsJob.
/// </summary>
public interface ISparkPaymentLauncher
{
    void Launch(SparkPayment payment);
}

public sealed class SparkPaymentLauncher : ISparkPaymentLauncher
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<SparkPaymentLauncher> _logger;

    public SparkPaymentLauncher(IServiceScopeFactory scopes, ILogger<SparkPaymentLauncher> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    public void Launch(SparkPayment payment) => _ = Task.Run(() => PayAsync(payment));

    /// <summary>
    /// Pays in a scope of its own, as the request that created the swap may be over. A failure here is only
    /// logged: MonitorSwapsJob finds the payment by its hash, and fails the swap once its invoice has expired.
    /// </summary>
    internal async Task PayAsync(SparkPayment payment)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<ISparkSwapService>().PayAsync(payment);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Spark swap {SwapId}: its payment could not be followed; MonitorSwapsJob takes over", payment.SwapId);
        }
    }
}
