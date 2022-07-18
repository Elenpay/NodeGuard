using Humanizer;

namespace FundsManager.Data.Models
{
    public class Key : Entity
    {
        public string Name { get; set; }

        public string XPUB { get; set; }

        public string? Description { get; set; }

        public bool IsArchived { get; set; }

        public bool IsCompromised { get; set; }

        #region Relationships

        public string? UserId { get; set; }
        public ApplicationUser? User { get; set; }

        public ICollection<Wallet> Wallets { get; set; }

        /// <summary>
        /// Indicates if the key comes from a internal wallet managed by the fundsmanager
        /// </summary>
        public bool IsFundsManagerPrivateKey { get; set; }

        #endregion Relationships

        public string GetTruncatedXPUBString()
        {
            return
                $"{XPUB.Substring(0, XPUB.Length / 2).Truncate(15, Truncator.FixedLength, TruncateFrom.Right)}{XPUB.Substring(XPUB.Length / 2 + 1, 20).Truncate(15, Truncator.FixedLength, TruncateFrom.Left)}";
        }
    }
}