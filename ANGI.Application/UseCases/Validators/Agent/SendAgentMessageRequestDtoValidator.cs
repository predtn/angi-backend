using ANGI.Application.Common.Models.Agent;
using ANGI.Application.DTOs.Agent;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Agent;

/// <summary>Validates transport identity, content and explicit UI state consent.</summary>
public sealed class SendAgentMessageRequestDtoValidator : AbstractValidator<SendAgentMessageRequestDto>
{
    /// <summary>Bounds inputs and rejects null strings before any database or LLM work.</summary>
    public SendAgentMessageRequestDtoValidator(AgentLimits limits)
    {
        RuleFor(x => x.ClientMessageId).GreaterThan(0).WithMessage("Mã tin nhắn phải là số nguyên dương.")
            .When(x => x.ClientMessageId.HasValue);
        RuleFor(x => x.ExpectedConversationVersion).GreaterThan(0).WithMessage("Phiên bản hội thoại phải lớn hơn 0.")
            .When(x => x.ExpectedConversationVersion.HasValue);
        RuleFor(x => x.Content).NotEmpty().WithMessage("Vui lòng nhập nội dung tin nhắn.");
        RuleFor(x => x.Content).MaximumLength(limits.MaxMessageLength).WithMessage($"Tin nhắn không được vượt quá {limits.MaxMessageLength} ký tự.")
            .When(x => !string.IsNullOrWhiteSpace(x.Content));
        RuleFor(x => x.SelectedItemKey).NotEmpty().WithMessage("Khóa món đã chọn không được để trống.")
            .MaximumLength(100).WithMessage("Khóa món không được vượt quá 100 ký tự.").When(x => x.SelectedItemKey is not null);
        RuleFor(x => x.Command).NotEmpty().WithMessage("Vui lòng cung cấp lệnh hội thoại.");
        RuleFor(x => x.Command).Must(x => x is "chat" or "alternative" or "discard" or "reset").WithMessage("Lệnh hội thoại không hợp lệ.")
            .When(x => !string.IsNullOrWhiteSpace(x.Command));
        RuleFor(x => x.ConfirmedState!).SetValidator(new AgentStateValidator()).When(x => x.ConfirmedState is not null);
        RuleFor(x => x.ConfirmedState).Must(state => state is null || state.PendingStateChange is null &&
            state.SourceRoadmapVersion is null && state.HistoryStartSequence is null)
            .WithMessage("Không được gửi metadata state do backend quản lý.");
    }
}
