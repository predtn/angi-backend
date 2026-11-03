using ANGI.Application.DTOs.Agent;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Agent;

/// <summary>Requires an exact positive immutable proposal identity.</summary>
public sealed class ConfirmAgentProposalRequestDtoValidator : AbstractValidator<ConfirmAgentProposalRequestDto>
{
    /// <summary>Rejects omitted and nonpositive IDs or versions.</summary>
    public ConfirmAgentProposalRequestDtoValidator()
    {
        RuleFor(x => x.ProposalId).GreaterThan(0).WithMessage("Mã đề xuất phải lớn hơn 0.");
        RuleFor(x => x.ProposalVersion).GreaterThan(0).WithMessage("Phiên bản đề xuất phải lớn hơn 0.");
        // Cùng giới hạn với AgentStateValidator: ngày kết thúc (tối đa 30 ngày sau) không được vượt DateOnly.
        RuleFor(x => x.StartDate).Must(date => !date.HasValue || date.Value.DayNumber <= DateOnly.MaxValue.DayNumber - 29)
            .WithMessage("Ngày kết thúc vượt giới hạn.");
    }
}
