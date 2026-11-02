namespace ANGI.Application.DTOs.Agent;

/// <summary>Confirms exactly one displayed immutable proposal revision.</summary>
public sealed class ConfirmAgentProposalRequestDto
{
    public long ProposalId { get; set; }
    public int ProposalVersion { get; set; }
    /// <summary>Start date chosen at save time; omitted keeps the date the conversation already holds, if any.</summary>
    public DateOnly? StartDate { get; set; }
}
