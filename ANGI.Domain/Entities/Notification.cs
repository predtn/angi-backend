using ANGI.Domain.Common;

namespace ANGI.Domain.Entities
{
    public class Notification : BaseEntity<long>, IHasCreatedAt
    {
        public int RecipientId { get; set; }
        public string Type { get; set; } = null!;
        public string Title { get; set; } = null!;
        public string? Body { get; set; }
        public string Data { get; set; } = "{}";
        public DateTime? ReadAt { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}
