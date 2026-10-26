using ANGI.Application.Common.Models.Agent;

namespace ANGI.Application.Common.Exceptions;

/// <summary>Returns actionable validation details when a preview can no longer be committed.</summary>
public sealed class AgentProposalValidationException : ConflictException
{
    public AgentValidation Validation { get; }

    /// <summary>Preserves deterministic errors so the frontend can request a revised proposal.</summary>
    public AgentProposalValidationException(AgentValidation validation)
        : base("PROPOSAL_NOT_COMMITTABLE", "Đề xuất cần được kiểm tra và tạo lại trước khi xác nhận.") => Validation = validation;
}
