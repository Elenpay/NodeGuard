namespace FundsManager.Data
{
    public class Key : Entity
    {
        public string Name { get; set; }

        public string XPUB { get; set; }

        public string Description { get; set; }

        public bool IsArchived { get; set; }

        public bool IsCompromised { get; set; }

        #region Relationships
        
        public string UserId { get; set; }
        public ApplicationUser User { get; set; }

        public ICollection<Wallet> Wallets { get; set; }

        #endregion 
    }
}
