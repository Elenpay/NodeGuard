using System.ComponentModel.DataAnnotations;

namespace FundsManager.Data.Models
{
    public class Channel : Entity
    {
        public string FundingTx { get; set; }
        public uint FundingTxOutputIndex { get; set; }
        public string? ChannelId { get; set; }
        public decimal Capacity { get; set; }

        public string? BtcCloseAddress { get; set; }

        #region Relationships

        public ICollection<ChannelOperationRequest> ChannelOperationRequests { get; set; }

        #endregion

    }
}