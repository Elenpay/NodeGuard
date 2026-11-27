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
using NodeGuard.Data.Repositories.Interfaces;

namespace NodeGuard.Services.Spark;

/// <summary>The saved Spark signer identities, in memory, with the repository's insert-once semantics.</summary>
internal sealed class InMemorySparkSignerIdentities : ISparkSignerIdentityRepository
{
    private readonly List<SparkSignerIdentity> _rows = [];

    public IReadOnlyList<SparkSignerIdentity> Rows => _rows;

    /// <summary>Counts GetAsync calls, to check the saved identity is cached.</summary>
    public int Reads { get; private set; }

    /// <summary>Saved by "another NodeGuard" right after the next read finds nothing, to exercise the insert race.</summary>
    public string? SavedConcurrently { get; set; }

    public InMemorySparkSignerIdentities Saved(string seedFingerprint, int account, string identity)
    {
        _rows.Add(new SparkSignerIdentity
        {
            Id = _rows.Count + 1,
            SeedFingerprint = seedFingerprint,
            Account = account,
            IdentityPublicKey = identity,
            CreationDatetime = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero)
        });
        return this;
    }

    public Task<SparkSignerIdentity?> GetAsync(string seedFingerprint, int account)
    {
        Reads++;
        return Task.FromResult(Find(seedFingerprint, account));
    }

    public Task<(SparkSignerIdentity Saved, bool Created)> GetOrAddAsync(string seedFingerprint, int account, string identityPublicKey)
    {
        if (SavedConcurrently is { } other)
        {
            Saved(seedFingerprint, account, other);
            SavedConcurrently = null;
        }

        if (Find(seedFingerprint, account) is { } existing) return Task.FromResult((existing, false));

        Saved(seedFingerprint, account, identityPublicKey);
        return Task.FromResult((Find(seedFingerprint, account)!, true));
    }

    private SparkSignerIdentity? Find(string seedFingerprint, int account) =>
        _rows.FirstOrDefault(r => r.SeedFingerprint == seedFingerprint && r.Account == account);
}
