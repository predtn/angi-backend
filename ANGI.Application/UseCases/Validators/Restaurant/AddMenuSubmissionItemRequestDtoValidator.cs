using ANGI.Application.DTOs.Restaurant;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Restaurant;

/// <summary>Validates the complete proposed dish accepted by OWN-14.</summary>
public sealed class AddMenuSubmissionItemRequestDtoValidator : AbstractValidator<AddMenuSubmissionItemRequestDto>
{
    private static readonly string[] DishForms = ["soup", "dry", "other"];

    /// <summary>Creates rules matching the documented dish proposal contract.</summary>
    public AddMenuSubmissionItemRequestDtoValidator()
    {
        RuleFor(request => request.Name)
            .NotEmpty().WithMessage("Tên món không được để trống.");
        // Format rules only for a value that was sent, so a missing one gets the single message above.
        RuleFor(request => request.Name)
            .Must(value => value is not null && value.Trim().Length <= 200)
            .WithMessage("Tên món không được vượt quá 200 ký tự.")
            .When(request => !string.IsNullOrWhiteSpace(request.Name));
        RuleFor(request => request.Description)
            .NotEmpty().WithMessage("Mô tả món không được để trống.");
        // Format rules only for a value that was sent, so a missing one gets the single message above.
        RuleFor(request => request.Description)
            .Must(value => value is not null && value.Trim().Length is >= 30 and <= 2000)
            .WithMessage("Mô tả món phải từ 30 đến 2000 ký tự.")
            .When(request => !string.IsNullOrWhiteSpace(request.Description));
        RuleFor(request => request.DishForm)
            .Must(value => DishForms.Contains(value, StringComparer.OrdinalIgnoreCase))
            .WithMessage("Loại món phải là soup, dry hoặc other.");
        RuleFor(request => request.Price)
            .NotNull().WithMessage("Giá món không được để trống.")
            .GreaterThanOrEqualTo(0).WithMessage("Giá món phải lớn hơn hoặc bằng 0.")
            .Must(BeValidVndAmount).WithMessage("Giá món phải là số nguyên VND có tối đa 12 chữ số.");
        RuleFor(request => request.CoverMediaId)
            .GreaterThan(0).WithMessage("Mã ảnh món phải lớn hơn 0.")
            .When(request => request.CoverMediaId.HasValue);
        RuleFor(request => request.TagIds)
            .Must(ids => ids is null || ids.All(id => id > 0) && ids.Distinct().Count() == ids.Count)
            .WithMessage("Danh sách tag phải gồm các mã dương và không trùng nhau.");
        RuleFor(request => request.SortOrder)
            .GreaterThanOrEqualTo((short)0).WithMessage("Thứ tự món phải lớn hơn hoặc bằng 0.")
            .When(request => request.SortOrder.HasValue);
    }

    /// <summary>Accepts whole-number VND amounts that fit the numeric(12,0) database column.</summary>
    private static bool BeValidVndAmount(decimal? price) =>
        !price.HasValue ||
        price.Value == decimal.Truncate(price.Value) && price.Value <= 999_999_999_999m;
}
