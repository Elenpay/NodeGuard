using Microsoft.AspNetCore.Identity;

namespace FundsManager.Data
{
    public class ApplicationUser : IdentityUser
    {

        
        #region Relationships 
        public ICollection<Key> Keys { get; set; }
        public ICollection<ChannelOperationRequest> ChannelOperationRequests { get; set; }
        public ICollection<Node> Nodes { get; set; }

        #endregion
    }
}

  
