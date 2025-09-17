// NodeGuard
// Copyright (C) 2025  Elenpay
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY, without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Affero General Public License for more details.
//
// You should have received a copy of the GNU Affero General Public License
// along with this program.  If not, see http://www.gnu.org/licenses/.

using Microsoft.AspNetCore.Cryptography.KeyDerivation;

namespace NodeGuard.Data.Models;

public class APIToken : Entity
{
    public required string Name { get; set; }
    public required string TokenHash { get; set; }
    public bool IsBlocked { get; set; }

    #region Relationships

    public required string CreatorId { get; set; }
    public required ApplicationUser Creator { get; set; }
    // Not using it actively atm but could be helpful if we decide to use it in the future
    public DateTime? ExpirationDate { get; set; }

    #endregion Relationships

    public void GenerateTokenHash(string password, string salt)
    {

        var hashed = Convert.ToBase64String(KeyDerivation.Pbkdf2(
        password: password!,
        salt: Convert.FromBase64String(salt),
        prf: KeyDerivationPrf.HMACSHA256,
        iterationCount: 100000,
        numBytesRequested: 256 / 8));

        TokenHash = hashed;
    }


}
