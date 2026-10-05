using ANGI.Domain.Common;

namespace ANGI.Domain.Entities
{
    public class Roadmap : BaseEntity<long>, IHasCreatedAt, IHasUpdatedAt, ISoftDelete
    {
        public int UserId { get; set; }
        public string Title { get; set; } = null!;
        public DateOnly? StartDate { get; set; }
        public short NumDays { get; set; }
        public decimal? BudgetAmount { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime? DeletedAt { get; set; }

        public ICollection<RoadmapDay> Days { get; set; } = new List<RoadmapDay>();
    }
}
