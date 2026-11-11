using ANGI.Domain.Common;
using ANGI.Domain.Enums;

namespace ANGI.Domain.Entities;

/// <summary>Persists an ordered logical turn and its completed replay response.</summary>
public sealed class AgentMessage : BaseEntity<long>, IHasCreatedAt
{
    public long ConversationId { get; set; }
    public int SequenceNo { get; set; }
    public long TurnId { get; set; }
    public long? ClientMessageId { get; set; }
    public AgentMessageRole Role { get; set; }
    public string Payload { get; set; } = "{}";
    public AgentProcessingStatus? ProcessingStatus { get; set; }
    public string? RequestHash { get; set; }
    public string? ResponsePayload { get; set; }
    public DateTime CreatedAt { get; set; }
}
