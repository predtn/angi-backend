using ANGI.Application.DTOs.Notification;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Notification;

/// <summary>Validates NOTI-03: mark notifications as read request payload.</summary>
public sealed class MarkNotificationAsReadRequestDtoValidator : AbstractValidator<MarkNotificationAsReadRequestDto>
{
    public const int MaxIds = 100;

    public MarkNotificationAsReadRequestDtoValidator()
    {
        RuleFor(x => x)
            .Must(x => x.All == true || (x.Ids is not null && x.Ids.Count > 0))
            .WithMessage("Phải chọn ít nhất một thông báo hoặc chọn tất cả.")
            .WithName("ids");

        RuleFor(x => x.Ids)
            .Must(ids => ids is null || ids.Count <= MaxIds)
            .WithMessage($"Danh sách thông báo không được vượt quá {MaxIds} phần tử.");

        RuleForEach(x => x.Ids)
            .GreaterThan(0)
            .WithMessage("Mã thông báo phải lớn hơn 0.");
    }
}
