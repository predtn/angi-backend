namespace ANGI.Application.DTOs.Agent;

/// <summary>Confirms exactly one displayed immutable proposal revision.</summary>
public sealed class ConfirmAgentProposalRequestDto
{
    public long ProposalId { get; set; }
    public int ProposalVersion { get; set; }
}
