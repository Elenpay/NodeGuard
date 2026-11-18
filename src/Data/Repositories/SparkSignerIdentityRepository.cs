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

public class SparkSignerIdentityRepository : ISparkSignerIdentityRepository
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;

    public SparkSignerIdentityRepository(IDbContextFactory<ApplicationDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    public async Task<SparkSignerIdentity?> GetAsync(string seedFingerprint, int account)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        return await context.SparkSignerIdentities.AsNoTracking()
            .FirstOrDefaultAsync(x => x.SeedFingerprint == seedFingerprint && x.Account == account);
    }

    public async Task<(SparkSignerIdentity Saved, bool Created)> GetOrAddAsync(string seedFingerprint, int account,
        string identityPublicKey)
    {
        if (await GetAsync(seedFingerprint, account) is { } existing) return (existing, false);

        var row = new SparkSignerIdentity
        {
            SeedFingerprint = seedFingerprint,
            Account = account,
            IdentityPublicKey = identityPublicKey
        };
        row.SetCreationDatetime();
        row.SetUpdateDatetime();

        try
        {
            await using var context = await _dbContextFactory.CreateDbContextAsync();
            await context.SparkSignerIdentities.AddAsync(row);
            await context.SaveChangesAsync();
            return (row, true);
        }
        catch (DbUpdateException)
        {
            // Another process saved one first and the unique index refused this row: that one stands
            if (await GetAsync(seedFingerprint, account) is { } first) return (first, false);
            throw;
        }
    }
}
