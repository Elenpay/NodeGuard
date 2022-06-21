
using System.ComponentModel.DataAnnotations;

namespace FundsManager.Data
{
    public class Node : Entity
    {
        public string PubKey { get; set; }

        public string Name { get; set; }

        public string Description { get; set; }

        #region Relationships

        public ICollection<ChannelOperationRequest> ChannelOperationRequestsAsSource { get; set; }
        public ICollection<ChannelOperationRequest> ChannelOperationRequestsAsDestination { get; set; }
        public ICollection<ApplicationUser> Users { get; set; }

        #endregion
    }
}