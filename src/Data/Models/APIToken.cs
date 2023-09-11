using Microsoft.AspNetCore.Cryptography.KeyDerivation;

namespace NodeGuard.Data.Models;

public class APIToken: Entity
{
    public string Name { get; set; }
    public string TokenHash { get; set; }
    public bool IsBlocked { get; set; }
    
    #region Relationships
    
    public string CreatorId { get; set; }
    public ApplicationUser Creator { get; set; }
    // Not using it actively atm but could be helpful if we decide to use it in the future
    public DateTime? ExpirationDate { get; set; }
    
    #endregion Relationships
    
    public void GenerateTokenHash(string password, string salt)
    {

        var hashed = Convert.ToBase64String(KeyDerivation.Pbkdf2(
        password: password!,
        salt:  Convert.FromBase64String(salt),
        prf: KeyDerivationPrf.HMACSHA256,
        iterationCount: 100000,
        numBytesRequested: 256 / 8));
        
        TokenHash = hashed;
    }


}