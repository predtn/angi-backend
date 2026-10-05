using ANGI.Domain.Common;
using ANGI.Domain.Enums;

namespace ANGI.Domain.Entities
{
    public class RoadmapGenerationJob : BaseEntity<long>, IHasCreatedAt
    {
        public long RoadmapId { get; set; }
        public int RequestedBy { get; set; }
        public GenerationScope Scope { get; set; } = GenerationScope.Full;
        public string Input { get; set; } = null!;
        public Guid? RecoRequestId { get; set; }
        public string? ModelName { get; set; }
        public string? PromptVersion { get; set; }
        public string? RawOutput { get; set; }
        public GenerationJobStatus Status { get; set; } = GenerationJobStatus.Queued;
        public string? ErrorMessage { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? FinishedAt { get; set; }

        public Roadmap Roadmap { get; set; } = null!;
    }
}
