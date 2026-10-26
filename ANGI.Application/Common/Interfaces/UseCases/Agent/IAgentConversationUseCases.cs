using ANGI.Application.DTOs.Agent;

namespace ANGI.Application.Common.Interfaces.UseCases.Agent;

/// <summary>Opens owned short-term Agent conversations.</summary>
public interface ICreateAgentConversationUseCase
{
    /// <summary>Validates an optional roadmap and persists a new empty conversation.</summary>
    Task<AgentConversationDto> ExecuteAsync(CreateAgentConversationRequestDto request, CancellationToken ct);
}

/// <summary>Restores owned conversation state without a write.</summary>
public interface IGetAgentConversationUseCase
{
    /// <summary>Loads completed history and the current immutable preview.</summary>
    Task<AgentConversationDto> ExecuteAsync(long id, CancellationToken ct);
}

/// <summary>Processes fenced, idempotent conversational turns.</summary>
public interface ISendAgentMessageUseCase
{
    /// <summary>Claims a short lease, executes outside transactions and atomically stores the response.</summary>
    Task<AgentTurnDto> ExecuteAsync(long id, SendAgentMessageRequestDto request, CancellationToken ct);
}

/// <summary>Confirms an exact proposal independently of the LLM.</summary>
public interface IConfirmAgentProposalUseCase
{
    /// <summary>Revalidates and commits once, or replays the original committed result.</summary>
    Task<AgentCommitDto> ExecuteAsync(long id, ConfirmAgentProposalRequestDto request, CancellationToken ct);
}

/// <summary>Expires and removes short-term Agent data while preserving saved roadmaps.</summary>
public interface ICleanupAgentConversationsUseCase
{
    /// <summary>Processes one bounded batch of expired or closed conversations.</summary>
    Task<int> ExecuteAsync(CancellationToken ct);
}
