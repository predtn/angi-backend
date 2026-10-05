using ANGI.Domain.Common;
using ANGI.Domain.Enums;

namespace ANGI.Domain.Entities
{
    public class Blog : BaseEntity<long>, IHasCreatedAt, IHasUpdatedAt, ISoftDelete
    {
        public int AuthorId { get; set; }
        public long? RoadmapId { get; set; }
        public string? RoadmapSnapshot { get; set; }
        public string Title { get; set; } = null!;
        public string Slug { get; set; } = null!;
        public string Content { get; set; } = null!;
        public long? CoverMediaId { get; set; }
        public BlogStatus Status { get; set; } = BlogStatus.Published;
        public DateTime PublishedAt { get; set; }
        public int LikeCount { get; set; }
        public int CommentCount { get; set; }
        public int ViewCount { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime? DeletedAt { get; set; }

        public User Author { get; set; } = null!;
        public MediaFile? CoverMedia { get; set; }
        public ICollection<BlogImage> Images { get; set; } = new List<BlogImage>();
        public ICollection<BlogComment> Comments { get; set; } = new List<BlogComment>();
        public ICollection<BlogLike> Likes { get; set; } = new List<BlogLike>();
    }
}
