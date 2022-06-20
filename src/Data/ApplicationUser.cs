using Microsoft.AspNetCore.Identity;

namespace FundsManager.Data
{
    public enum ApplicationUserRole
    {
        NodeManager, TrustedFinanceUser, Superadmin
    }
    public class ApplicationUser : IdentityUser
    {


        #region Relationships

        public List<Key> Keys { get; set; }

        #endregion
    }
}

  
