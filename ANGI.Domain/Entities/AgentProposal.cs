using ANGI.Domain.Common;
using ANGI.Domain.Enums;

namespace ANGI.Domain.Entities;

/// <summary>Stores a validated immutable snapshot and its idempotent commit result.</summary>
public sealed class AgentProposal : BaseEntity<long>, IHasCreatedAt
{
    public long ConversationId { get; set; }
    public int ProposalVersion { get; set; }
    public AgentProposalOperation Operation { get; set; }
    public long? BaseRoadmapId { get; set; }
    public DateTime? BaseRoadmapUpdatedAt { get; set; }
    public string Payload { get; set; } = "{}";
    public string ValidationResult { get; set; } = "{}";
    public AgentProposalStatus Status { get; set; }
    public Guid[] RecoRequestIds { get; set; } = [];
    public long? ResultRoadmapId { get; set; }
    public DateTime? ResultRoadmapUpdatedAt { get; set; }
    public string? CommitResult { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? CommittedAt { get; set; }
}
