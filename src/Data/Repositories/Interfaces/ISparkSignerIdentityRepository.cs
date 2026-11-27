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

namespace NodeGuard.Data.Repositories.Interfaces;

public interface ISparkSignerIdentityRepository
{
    /// <summary>The identity saved for the seed and account, if any.</summary>
    Task<SparkSignerIdentity?> GetAsync(string seedFingerprint, int account);

    /// <summary>
    /// Saves <paramref name="identityPublicKey"/> for the seed and account unless one is already saved, and returns the
    /// saved row: an existing row is never overwritten, even when another process saves first.
    /// </summary>
    /// <returns>The saved row, and whether this call created it.</returns>
    Task<(SparkSignerIdentity Saved, bool Created)> GetOrAddAsync(string seedFingerprint, int account, string identityPublicKey);
}
