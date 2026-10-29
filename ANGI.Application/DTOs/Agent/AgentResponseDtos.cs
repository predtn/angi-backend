using ANGI.Application.Common.Models.Agent;

namespace ANGI.Application.DTOs.Agent;

/// <summary>Restores durable trip state, completed history and processing status.</summary>
public sealed record AgentConversationDto(long ConversationId, long Version, string Status, AgentState State,
    bool Processing, AgentTranscript[] Messages, AgentProposalDto? CurrentProposal);

/// <summary>Represents a validated immutable preview.</summary>
public sealed record AgentProposalDto(long ProposalId, int Version, string Operation, string Status,
    AgentSnapshot Snapshot, AgentValidation Validation, DateTime ExpiresAt);

/// <summary>Represents a stored completed response or the status of an in-flight retry.</summary>
public sealed record AgentTurnDto(string Status, string Reply, long ConversationVersion,
    AgentProposalDto? Proposal = null, AgentValidation? Validation = null, bool Replayed = false, AgentCommitDto? Commit = null,
    long? ClientMessageId = null);

/// <summary>Returns the original commit revision and durable proposal-to-item mapping.</summary>
public sealed record AgentCommitDto(long RoadmapId, long RoadmapVersion, long CommittedProposalId,
    IReadOnlyDictionary<string, long> ItemIds);
