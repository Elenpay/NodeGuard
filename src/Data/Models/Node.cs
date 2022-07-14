using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace FundsManager.Data.Models
{
    public class Node : Entity
    {
        public string PubKey { get; set; }

        public string Name { get; set; }

        public string? Description { get; set; }
        /// <summary>
        /// Macaroon with channel admin permissions
        /// </summary>
        public string? ChannelAdminMacaroon { get; set; }

        /// <summary>
        ///host:port grpc endpoint
        /// </summary>
        public string? Endpoint { get; set; }

        /// <summary>
        /// Returns true if the node is managed by us. We defer this from the existence of an Endpoint
        /// </summary>
        [NotMapped]
        public bool IsManaged => Endpoint != null;
        
        #region Relationships

        public ICollection<ChannelOperationRequest> ChannelOperationRequestsAsSource { get; set; }
        public ICollection<ChannelOperationRequest> ChannelOperationRequestsAsDestination { get; set; }
        public ICollection<ApplicationUser> Users { get; set; }

        #endregion
    }
}