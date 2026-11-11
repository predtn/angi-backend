using ANGI.Domain.Common;

namespace ANGI.Domain.Entities
{
    public class BlogLike : IHasCreatedAt
    {
        public long BlogId { get; set; }
        public int UserId { get; set; }
        public DateTime CreatedAt { get; set; }

        public Blog Blog { get; set; } = null!;
    }
}
