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

namespace NodeGuard.Data.Models;

/// <summary>
/// The Spark identity the remote signer served for a seed and Spark account, saved on the first successful
/// /spark/info handshake (SPARK_SIGNER=remote). Every later handshake must serve the same identity. Keyed by
/// (<see cref="SeedFingerprint"/>, <see cref="Account"/>); deleting the row is how the saved identity is reset.
/// </summary>
public class SparkSignerIdentity : Entity
{
    /// <summary>The remote signer's seed (SPARK_SEED_FINGERPRINT), lowercase hex.</summary>
    public string SeedFingerprint { get; set; } = null!;

    /// <summary>The Spark account index (SPARK_ACCOUNT).</summary>
    public int Account { get; set; }

    /// <summary>The Spark identity public key the signer served (compressed, lowercase hex).</summary>
    public string IdentityPublicKey { get; set; } = null!;
}
