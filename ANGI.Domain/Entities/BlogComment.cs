using ANGI.Domain.Common;
using ANGI.Domain.Enums;

namespace ANGI.Domain.Entities
{
    public class BlogComment : BaseEntity<long>, IHasCreatedAt, IHasUpdatedAt, ISoftDelete
    {
        public long BlogId { get; set; }
        public int UserId { get; set; }
        public string Content { get; set; } = null!;
        public ContentStatus Status { get; set; } = ContentStatus.Visible;
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime? DeletedAt { get; set; }

        public Blog Blog { get; set; } = null!;
        public User User { get; set; } = null!;
    }
}
