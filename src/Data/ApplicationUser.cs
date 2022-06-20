using Microsoft.AspNetCore.Identity;

namespace FundsManager.Data
{
    public class ApplicationUser : IdentityUser
    {


        #region Relationships

        public List<Key> Keys { get; set; }

        #endregion
    }
}

  
