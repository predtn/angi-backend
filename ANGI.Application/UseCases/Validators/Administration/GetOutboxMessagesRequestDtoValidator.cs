using ANGI.Application.DTOs.Administration;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Administration;

/// <summary>Validates OPS-01 outbox filtering and pagination parameters.</summary>
public sealed class GetOutboxMessagesRequestDtoValidator : AbstractValidator<GetOutboxMessagesRequestDto>
{
    private static readonly string[] AllowedStatuses = ["pending", "processing", "done", "dead", "failed"];
    private static readonly string[] AllowedEventTypes = ["DISH_UPSERTED", "DISH_DEACTIVATED", "FEEDBACK_CREATED"];

    /// <summary>Creates validation rules for status, event type, and pagination.</summary>
    public GetOutboxMessagesRequestDtoValidator()
    {
        RuleFor(request => request.Status)
            .Must(status => status is null || AllowedStatuses.Contains(status.Trim().ToLowerInvariant()))
            .WithMessage("status không hợp lệ. Cho phép: pending, processing, done, dead, failed.");

        RuleFor(request => request.EventType)
            .Must(eventType => eventType is null || AllowedEventTypes.Contains(eventType.Trim().ToUpperInvariant()))
            .WithMessage("eventType không hợp lệ. Cho phép: DISH_UPSERTED, DISH_DEACTIVATED, FEEDBACK_CREATED.");

        RuleFor(request => request.Page)
            .GreaterThanOrEqualTo(1).WithMessage("Trang phải lớn hơn hoặc bằng 1.");

        RuleFor(request => request.PageSize)
            .InclusiveBetween(1, 100).WithMessage("Kích thước trang phải từ 1 đến 100.");
    }
}
