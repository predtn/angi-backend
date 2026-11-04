using ANGI.Domain.Common;
using ANGI.Domain.Enums;

namespace ANGI.Domain.Entities
{
    public class Roadmap : BaseEntity<long>, IHasCreatedAt, IHasUpdatedAt, ISoftDelete
    {
        /// <summary>Longest trip a roadmap can hold; ck_roadmaps_num_days enforces the same value.</summary>
        public const short MaxNumDays = 100;

        public int UserId { get; set; }
        public string Title { get; set; } = null!;
        public DateOnly? StartDate { get; set; }
        public short NumDays { get; set; }
        public decimal? BudgetAmount { get; set; }
        public RoadmapOriginMode OriginMode { get; set; } = RoadmapOriginMode.None;
        public decimal? OriginLatitude { get; set; }
        public decimal? OriginLongitude { get; set; }
        public string? OriginLabel { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime? DeletedAt { get; set; }

        public ICollection<RoadmapDay> Days { get; set; } = new List<RoadmapDay>();
    }
}
