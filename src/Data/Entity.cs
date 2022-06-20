
using System.ComponentModel.DataAnnotations;

namespace FundsManager.Data
{
    public abstract class Entity
    {
        [Key]
        public int Id { get; set; }
        public DateTimeOffset CreationDatetime { get; set; }
        public DateTimeOffset UpdateDatetime { get; set; }
    }
}
