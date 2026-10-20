using ANGI.Application.DTOs.Notification;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Notification;

/// <summary>Validates query parameters for NOTI-01 notification listing.</summary>
public sealed class GetNotificationsRequestDtoValidator : AbstractValidator<GetNotificationsRequestDto>
{
    public GetNotificationsRequestDtoValidator()
    {
        RuleFor(x => x.Page)
            .GreaterThanOrEqualTo(1)
            .WithMessage("page phải lớn hơn hoặc bằng 1.");

        RuleFor(x => x.PageSize)
            .InclusiveBetween(1, 100)
            .WithMessage("pageSize phải nằm trong khoảng 1 đến 100.");

        RuleFor(x => x.Type)
            .MaximumLength(40)
            .When(x => !string.IsNullOrWhiteSpace(x.Type))
            .WithMessage("type không được vượt quá 40 ký tự.");
    }
}
