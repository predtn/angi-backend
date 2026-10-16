using ANGI.Application.DTOs.Roadmap;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Roadmap;

/// <summary>Validates the complete roadmap overview accepted by RM-01.</summary>
public sealed class CreateRoadmapRequestDtoValidator : AbstractValidator<CreateRoadmapRequestDto>
{
    private static readonly string[] _originModes = ["none", "pinned", "gps"];

    /// <summary>Creates field and cross-field rules from the RM-01 contract.</summary>
    public CreateRoadmapRequestDtoValidator()
    {
        RuleFor(x => x.Title)
            .NotEmpty().WithMessage("Tên roadmap không được để trống.")
            .MaximumLength(200).WithMessage("Tên roadmap không được vượt quá 200 ký tự.");
        RuleFor(x => x.NumDays)
            .InclusiveBetween((short)1, (short)30)
            .WithMessage("Số ngày phải từ 1 đến 30.");
        RuleFor(x => x.BudgetAmount)
            .GreaterThanOrEqualTo(0)
            .WithMessage("Ngân sách không được nhỏ hơn 0.")
            .When(x => x.BudgetAmount.HasValue);
        RuleFor(x => x.OriginMode)
            .Must(BeOriginMode)
            .WithMessage("Điểm xuất phát phải là none, pinned hoặc gps.")
            .When(x => x.OriginMode is not null);
        RuleFor(x => x.OriginLatitude)
            .InclusiveBetween(-90m, 90m)
            .WithMessage("Vĩ độ phải nằm trong khoảng -90 đến 90.")
            .When(x => x.OriginLatitude.HasValue);
        RuleFor(x => x.OriginLongitude)
            .InclusiveBetween(-180m, 180m)
            .WithMessage("Kinh độ phải nằm trong khoảng -180 đến 180.")
            .When(x => x.OriginLongitude.HasValue);
        RuleFor(x => x.OriginLabel)
            .MaximumLength(200)
            .WithMessage("Tên điểm xuất phát không được vượt quá 200 ký tự.")
            .When(x => x.OriginLabel is not null);
        RuleFor(x => x).Custom(ValidateOrigin);
    }

    /// <summary>Checks the three public snake-case origin values.</summary>
    private static bool BeOriginMode(string? value) =>
        value is not null && _originModes.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>Enforces coordinates only for pinned origins and requires both coordinates together.</summary>
    private static void ValidateOrigin(CreateRoadmapRequestDto request, ValidationContext<CreateRoadmapRequestDto> context)
    {
        var mode = request.OriginMode?.Trim().ToLowerInvariant() ?? "none";
        if (mode == "pinned")
        {
            if (!request.OriginLatitude.HasValue)
            {
                context.AddFailure(nameof(request.OriginLatitude), "Vĩ độ là bắt buộc khi điểm xuất phát là pinned.");
            }

            if (!request.OriginLongitude.HasValue)
            {
                context.AddFailure(nameof(request.OriginLongitude), "Kinh độ là bắt buộc khi điểm xuất phát là pinned.");
            }

            return;
        }

        if (request.OriginLatitude.HasValue)
        {
            context.AddFailure(nameof(request.OriginLatitude), "Vĩ độ phải để trống khi điểm xuất phát không phải pinned.");
        }

        if (request.OriginLongitude.HasValue)
        {
            context.AddFailure(nameof(request.OriginLongitude), "Kinh độ phải để trống khi điểm xuất phát không phải pinned.");
        }

        if (!string.IsNullOrWhiteSpace(request.OriginLabel))
        {
            context.AddFailure(nameof(request.OriginLabel), "Tên điểm xuất phát phải để trống khi điểm xuất phát không phải pinned.");
        }
    }
}
