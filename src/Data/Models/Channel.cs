using System.ComponentModel.DataAnnotations;

namespace FundsManager.Data.Models
{
    public class Channel : Entity
    {
        public string ChannelPoint { get; set; }
        public string ChannelId { get; set; }
        public string Capacity { get; set; }

        #region Relationships

        public ICollection<ChannelOperationRequest> ChannelOperationRequests { get; set; }

        #endregion

    }
}