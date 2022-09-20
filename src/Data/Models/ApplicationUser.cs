using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Identity;

namespace FundsManager.Data.Models
{
    [Flags]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ApplicationUserRole
    {
        NodeManager, FinanceManager, Superadmin
    }

    public class ApplicationUser : IdentityUser
    {
        [NotMapped]
        public bool IsLocked => LockoutEnabled && LockoutEnd != null;

        #region Relationships

        public ICollection<Key> Keys { get; set; }
        public ICollection<ChannelOperationRequest> ChannelOperationRequests { get; set; }
        public ICollection<Node> Nodes { get; set; }
        public ICollection<WalletWithdrawalRequest> WalletWithdrawalRequests { get; set; }

        #endregion Relationships
    }
}