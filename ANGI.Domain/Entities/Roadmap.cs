using ANGI.Domain.Common;
using ANGI.Domain.Enums;

namespace ANGI.Domain.Entities
{
    public class Roadmap : BaseEntity<long>, IHasCreatedAt, IHasUpdatedAt, ISoftDelete
    {
        /// <summary>Longest trip RM-01, RM-04 and the agent accept for a new or changed number of days (team decision: 7 days).</summary>
        public const short MaxNumDays = 7;

        /// <summary>
        /// Longest trip ck_roadmaps_num_days stores. Roadmaps created under the earlier 100-day limit keep their days, and every
        /// change to a day or item updates the roadmap row, so the database check stays wider than MaxNumDays.
        /// </summary>
        public const short MaxStoredNumDays = 100;

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
