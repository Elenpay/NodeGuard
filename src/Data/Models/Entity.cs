using System.ComponentModel.DataAnnotations;

namespace FundsManager.Data.Models
{
    public abstract class Entity
    {
        [Key]
        public int Id { get; set; }

        public DateTimeOffset CreationDatetime { get; set; }
        public DateTimeOffset UpdateDatetime { get; set; }

        public virtual void SetCreationDatetime()
        {
            CreationDatetime = DateTimeOffset.UtcNow;
        }

        public virtual void SetUpdateDatetime()
        {
            UpdateDatetime = DateTimeOffset.UtcNow;
        }

        public override string ToString()
        {
            return $"{Id}";
        }
    }
}