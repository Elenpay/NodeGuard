using Humanizer;

namespace FundsManager.Data.Models
{
    public class Key : Entity, IEquatable<Key>
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

        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != typeof(Key)) return false;
            return Equals((Key)obj);
        }

        public bool Equals(Key? other)
        {
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;
            return XPUB == other.XPUB;
        }

        public override int GetHashCode()
        {
            return XPUB.GetHashCode();
        }

        public static bool operator ==(Key? left, Key? right)
        {
            return Equals(left, right);
        }

        public static bool operator !=(Key? left, Key? right)
        {
            return !Equals(left, right);
        }
    }
}