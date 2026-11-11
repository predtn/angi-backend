using ANGI.Application.DTOs.Discovery;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Discovery;

/// <summary>Validates IMP-01: a request id and 1..200 dish ids.</summary>
public sealed class RecordImpressionsRequestDtoValidator : AbstractValidator<RecordImpressionsRequestDto>
{
    public const int MaxDishIds = 200;

    public RecordImpressionsRequestDtoValidator()
    {
        RuleFor(x => x.RequestId)
            .Cascade(CascadeMode.Stop)
            .NotNull().WithMessage("requestId là bắt buộc.")
            .NotEqual(Guid.Empty).WithMessage("requestId không hợp lệ.");

        RuleFor(x => x.DishIds)
            .Cascade(CascadeMode.Stop)
            .NotNull().WithMessage("dishIds là bắt buộc.")
            .Must(ids => ids!.Count is >= 1 and <= MaxDishIds)
                .WithMessage($"dishIds phải có từ 1 đến {MaxDishIds} món.");

        RuleForEach(x => x.DishIds)
            .GreaterThan(0).WithMessage("Mã món phải lớn hơn 0.");
    }
}
