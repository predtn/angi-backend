using ANGI.Application.DTOs.Moderation;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Moderation;

/// <summary>Validates the MOD-U3 payload for sanctioning accounts.</summary>
public sealed class CreateUserSanctionRequestDtoValidator : AbstractValidator<CreateUserSanctionRequestDto>
{
    public CreateUserSanctionRequestDtoValidator(TimeProvider clock)
    {
        RuleFor(x => x.Type)
            .NotEmpty().WithMessage("Vui lòng chọn hình thức xử phạt.");

        RuleFor(x => x.Type)
            .Must(t => t.Equals("warn", StringComparison.OrdinalIgnoreCase) ||
                       t.Equals("suspend", StringComparison.OrdinalIgnoreCase) ||
                       t.Equals("ban", StringComparison.OrdinalIgnoreCase))
            .WithMessage("Hình thức xử phạt không hợp lệ.")
            .When(x => !string.IsNullOrWhiteSpace(x.Type));

        RuleFor(x => x.Reason)
            .NotEmpty().WithMessage("Vui lòng nhập lý do xử phạt.");

        RuleFor(x => x.Reason)
            .MaximumLength(1000).WithMessage("Lý do xử phạt không được vượt quá 1000 ký tự.")
            .When(x => !string.IsNullOrWhiteSpace(x.Reason));

        When(x => !string.IsNullOrWhiteSpace(x.Type) && x.Type.Equals("suspend", StringComparison.OrdinalIgnoreCase), () =>
        {
            RuleFor(x => x)
                .Must(x => x.EndsAt.HasValue || (x.DurationDays.HasValue && x.DurationDays.Value > 0))
                .WithMessage("Vui lòng nhập thời hạn tạm khóa.");

            RuleFor(x => x.EndsAt)
                .Must(value => value > clock.GetUtcNow())
                .WithMessage("Thời điểm hết hạn tạm khóa phải ở tương lai.")
                .When(x => x.EndsAt.HasValue);

            RuleFor(x => x.DurationDays)
                .GreaterThan(0)
                .WithMessage("Số ngày tạm khóa phải lớn hơn 0.")
                .When(x => x.DurationDays.HasValue);
        });
    }
}
