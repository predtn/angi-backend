using ANGI.Application.DTOs.Agent;

namespace ANGI.Application.Common.Models.Agent;

/// <summary>Backend-selected proposal revision whose route preflight runs before the commit transaction.</summary>
public sealed record AgentCommitPreparation(long ConversationId, long ProposalId, int ProposalVersion,
    AgentSnapshot? Snapshot, AgentCommitDto? Replay = null);
