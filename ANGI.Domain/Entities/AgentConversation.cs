using ANGI.Domain.Common;
using ANGI.Domain.Enums;

namespace ANGI.Domain.Entities;

/// <summary>Owns expiring trip state and a fenced processing lease.</summary>
public sealed class AgentConversation : BaseEntity<long>, IHasCreatedAt, IHasUpdatedAt
{
    public int UserId { get; set; }
    public long? RoadmapId { get; set; }
    public AgentConversationStatus Status { get; set; } = AgentConversationStatus.Active;
    public string State { get; set; } = "{}";
    public long? CurrentProposalId { get; set; }
    public long Version { get; set; } = 1;
    public long? ProcessingTurnId { get; set; }
    public long? ProcessingAttemptId { get; set; }
    public DateTime? ProcessingUntil { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public AgentProposal? CurrentProposal { get; set; }
}
