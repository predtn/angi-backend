using ANGI.Domain.Common;

namespace ANGI.Domain.Entities
{
    public class RoadmapDay : BaseEntity<long>
    {
        public long RoadmapId { get; set; }
        public short DayNumber { get; set; }
        public string? Note { get; set; }

        public Roadmap Roadmap { get; set; } = null!;
        public ICollection<RoadmapItem> Items { get; set; } = new List<RoadmapItem>();
    }
}
