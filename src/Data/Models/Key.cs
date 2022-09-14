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

        public string MasterFingerprint { get; set; }

        /// <summary>
        /// Derivation Path (e.g.
        /// </summary>
        public string Path { get; set; }

        #region Relationships

        public string? UserId { get; set; }
        public ApplicationUser? User { get; set; }

        public ICollection<Wallet> Wallets { get; set; }

        /// <summary>
        /// Indicates if the key comes from a internal wallet managed by the fundsmanager
        /// </summary>
        public bool IsFundsManagerPrivateKey { get; set; }

        #endregion Relationships

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