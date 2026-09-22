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

using Microsoft.EntityFrameworkCore;
using NodeGuard.Data.Models;
using NodeGuard.Data.Repositories.Interfaces;

namespace NodeGuard.Data.Repositories;

public class PaymentRouteRepository : IPaymentRouteRepository
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;
    private readonly ILogger<PaymentRouteRepository> _logger;

    public PaymentRouteRepository(IDbContextFactory<ApplicationDbContext> dbContextFactory,
        ILogger<PaymentRouteRepository> logger)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    public async Task<(bool inserted, string? error)> UpsertAsync(PaymentRoute payment)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        try
        {
            var existing = await dbContext.PaymentRoutes
                .Include(p => p.Hops)
                .FirstOrDefaultAsync(p => p.PaymentHash == payment.PaymentHash);

            var now = DateTimeOffset.UtcNow;

            if (existing == null)
            {
                payment.CreationDatetime = now;
                payment.UpdateDatetime = now;
                await dbContext.PaymentRoutes.AddAsync(payment);
                await dbContext.SaveChangesAsync();
                return (true, null);
            }

            existing.OriginNodePubKey = payment.OriginNodePubKey;
            existing.Status = payment.Status;
            existing.CreatedAt = payment.CreatedAt;
            existing.AmountMsat = payment.AmountMsat;
            existing.Destination = payment.Destination;
            existing.UpdateDatetime = now;

            // Replace the hop set wholesale — each LND payment update carries the payment's full
            // attempt list, so the incoming snapshot supersedes what we stored.
            //
            // But never let an EMPTY snapshot erase hops we already captured. LND deletes failed
            // HTLC attempts once a payment is terminal (unless the node runs with
            // --keep-failed-payment-attempts), so any later read of the same payment can honestly
            // come back with no attempts at all. Wiping on that would destroy exactly the failed
            // routes this feature exists to show.
            if (payment.Hops.Count > 0)
            {
                dbContext.PaymentRouteHops.RemoveRange(existing.Hops);
                await dbContext.SaveChangesAsync();

                foreach (var hop in payment.Hops)
                {
                    hop.PaymentHash = existing.PaymentHash;
                }

                await dbContext.PaymentRouteHops.AddRangeAsync(payment.Hops);
            }

            await dbContext.SaveChangesAsync();
            return (false, null);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error saving payment route {PaymentHash}", payment.PaymentHash);
            return (false, e.Message);
        }
    }

    public async Task<List<PaymentRoute>> GetByCreatedAtRangeAsync(string originNodePubKey, DateTimeOffset start, DateTimeOffset end)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        return await dbContext.PaymentRoutes
            .Include(p => p.Hops)
            .Where(p => p.OriginNodePubKey == originNodePubKey && p.CreatedAt >= start && p.CreatedAt <= end)
            .ToListAsync();
    }
}
