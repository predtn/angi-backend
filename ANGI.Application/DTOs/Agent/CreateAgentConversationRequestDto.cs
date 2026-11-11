namespace ANGI.Application.DTOs.Agent;

/// <summary>Opens a new short-term conversation, optionally anchored to an owned roadmap.</summary>
public sealed class CreateAgentConversationRequestDto
{
    public long? RoadmapId { get; set; }
}
