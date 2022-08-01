using System.ComponentModel.DataAnnotations;

namespace FundsManager.Data.Models
{
    public class Channel : Entity
    {
        
        public enum ChannelStatus
        {
            Open = 1,
            Closed = 2
        }
        
        public string FundingTx { get; set; }
        public uint FundingTxOutputIndex { get; set; }
        public string? ChannelId { get; set; }

        /// <summary>
        /// Capacity in SATS
        /// </summary>
        public long SatsAmount { get; set; }

        public string? BtcCloseAddress { get; set; }

        public ChannelStatus Status { get; set; }

        #region Relationships

        public ICollection<ChannelOperationRequest> ChannelOperationRequests { get; set; }

        #endregion

    }
}