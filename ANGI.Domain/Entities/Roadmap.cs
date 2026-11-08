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
        /// <summary>
        /// The province or city the trip goes to, as the traveler chose it from the catalog (or as
        /// derived from the pinned start); null when unknown. Frames the map and scopes suggestions.
        /// </summary>
        public string? DestinationProvince { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime? DeletedAt { get; set; }

        public ICollection<RoadmapDay> Days { get; set; } = new List<RoadmapDay>();
    }
}
