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

using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace NodeGuard.Services.Spark;

/// <summary>Encrypts Spark wallets' mnemonics for the database.</summary>
public interface ISparkSeedProtector
{
    string Protect(string mnemonic);

    /// <exception cref="SparkUnavailableException">The ciphertext can't be decrypted (a lost or changed key ring).</exception>
    string Unprotect(string protectedMnemonic);
}

/// <summary>
/// ASP.NET Data Protection under its own purpose. The key ring is NodeGuard's, persisted in Postgres
/// (DataProtectionKeys) and shared by every replica; losing it, or changing the application name, makes the
/// mnemonics unrecoverable except from the words shown when each wallet was created.
/// </summary>
public sealed class SparkSeedProtector(IDataProtectionProvider provider) : ISparkSeedProtector
{
    public const string Purpose = "NodeGuard.SparkWallet.Mnemonic.v1";

    private readonly IDataProtector _protector = provider.CreateProtector(Purpose);

    public string Protect(string mnemonic) => _protector.Protect(mnemonic);

    public string Unprotect(string protectedMnemonic)
    {
        try
        {
            return _protector.Unprotect(protectedMnemonic);
        }
        catch (CryptographicException e)
        {
            throw new SparkUnavailableException($"cannot decrypt its seed ({e.Message})");
        }
    }
}
