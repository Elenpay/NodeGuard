using System.ComponentModel.DataAnnotations;

namespace FundsManager.Data.Models
{
    public class Node : Entity
    {
        public string PubKey { get; set; }

        public string Name { get; set; }

        public string? Description { get; set; }
        public string ChannelAdminMacaroon { get; set; }

        /// <summary>
        ///host:port grpc endpoint
        /// </summary>
        public string Endpoint { get; set; }

        #region Relationships

        public ICollection<ChannelOperationRequest> ChannelOperationRequestsAsSource { get; set; }
        public ICollection<ChannelOperationRequest> ChannelOperationRequestsAsDestination { get; set; }

        public ICollection<ApplicationUser> Users { get; set; }
        /// <summary>
        /// Macaroon with channel admin permissions
        /// </summary>

        #endregion Relationships
    }
}