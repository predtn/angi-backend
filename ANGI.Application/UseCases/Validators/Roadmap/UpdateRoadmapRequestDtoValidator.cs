using ANGI.Application.DTOs.Roadmap;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Roadmap;

/// <summary>Validates fields explicitly supplied to RM-04.</summary>
public sealed class UpdateRoadmapRequestDtoValidator : AbstractValidator<UpdateRoadmapRequestDto>
{
    private static readonly string[] _originModes = ["none", "pinned", "gps"];

    /// <summary>Creates partial-update rules while preserving omitted versus explicit null values.</summary>
    public UpdateRoadmapRequestDtoValidator()
    {
        RuleFor(x => x.Title)
            .NotEmpty().WithMessage("Tên roadmap không được để trống.")
            .MaximumLength(200).WithMessage("Tên roadmap không được vượt quá 200 ký tự.")
            .When(x => x.TitleSpecified);
        RuleFor(x => x.NumDays)
            .NotNull().WithMessage("Số ngày không được là null.")
            .InclusiveBetween((short)1, (short)30).WithMessage("Số ngày phải từ 1 đến 30.")
            .When(x => x.NumDaysSpecified);
        RuleFor(x => x.BudgetAmount)
            .GreaterThanOrEqualTo(0).WithMessage("Ngân sách không được nhỏ hơn 0.")
            .When(x => x.BudgetAmountSpecified && x.BudgetAmount.HasValue);
        RuleFor(x => x.OriginMode)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("Điểm xuất phát không được để trống.")
            .Must(BeOriginMode).WithMessage("Điểm xuất phát phải là none, pinned hoặc gps.")
            .When(x => x.OriginModeSpecified);
        RuleFor(x => x.OriginLatitude)
            .InclusiveBetween(-90m, 90m).WithMessage("Vĩ độ phải nằm trong khoảng -90 đến 90.")
            .When(x => x.OriginLatitudeSpecified && x.OriginLatitude.HasValue);
        RuleFor(x => x.OriginLongitude)
            .InclusiveBetween(-180m, 180m).WithMessage("Kinh độ phải nằm trong khoảng -180 đến 180.")
            .When(x => x.OriginLongitudeSpecified && x.OriginLongitude.HasValue);
        RuleFor(x => x.OriginLabel)
            .MaximumLength(200).WithMessage("Tên điểm xuất phát không được vượt quá 200 ký tự.")
            .When(x => x.OriginLabelSpecified && x.OriginLabel is not null);
    }

    /// <summary>Checks the three public snake-case origin values.</summary>
    private static bool BeOriginMode(string? value) =>
        value is not null && _originModes.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase);
}
