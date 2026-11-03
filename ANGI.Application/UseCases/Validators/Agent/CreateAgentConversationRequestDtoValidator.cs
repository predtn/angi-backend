using ANGI.Application.DTOs.Agent;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Agent;

/// <summary>Validates the optional owned roadmap reference.</summary>
public sealed class CreateAgentConversationRequestDtoValidator : AbstractValidator<CreateAgentConversationRequestDto>
{
    /// <summary>Requires positive resource IDs when present.</summary>
    public CreateAgentConversationRequestDtoValidator() => RuleFor(x => x.RoadmapId).GreaterThan(0)
        .WithMessage("Mã roadmap phải lớn hơn 0.").When(x => x.RoadmapId.HasValue);
}
