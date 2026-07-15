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

    public async Task<(bool inserted, string? error)> InsertIfNewAsync(PaymentRoute payment)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        try
        {
            // Idempotency: never re-insert a payment we already tracked (mirror of the
            // Python tracker's `db.get(Payment, pay_hash) is not None` check).
            if (await dbContext.PaymentRoutes.AnyAsync(p => p.PaymentHash == payment.PaymentHash))
            {
                return (false, null);
            }

            var now = DateTimeOffset.UtcNow;
            payment.CreationDatetime = now;
            payment.UpdateDatetime = now;
            await dbContext.PaymentRoutes.AddAsync(payment);
            await dbContext.SaveChangesAsync();
            return (true, null);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error saving payment route {PaymentHash}", payment.PaymentHash);
            return (false, e.Message);
        }
    }

    public async Task<List<PaymentRoute>> GetByCreatedAtRangeAsync(DateTimeOffset start, DateTimeOffset end)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        return await dbContext.PaymentRoutes
            .Include(p => p.Hops)
            .Where(p => p.CreatedAt >= start && p.CreatedAt <= end)
            .ToListAsync();
    }
}
