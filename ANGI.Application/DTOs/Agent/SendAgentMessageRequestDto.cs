namespace ANGI.Application.DTOs.Agent;

/// <summary>Accepts a chat prompt with optional identity and revision for retry-safe clients.</summary>
public sealed class SendAgentMessageRequestDto
{
    public long? ClientMessageId { get; set; }
    public long? ExpectedConversationVersion { get; set; }
    public string Content { get; set; } = string.Empty;
    public string? SelectedItemKey { get; set; }
    public string Command { get; set; } = "chat";
    public Common.Models.Agent.AgentState? ConfirmedState { get; set; }
}
