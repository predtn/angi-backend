using ANGI.Application.DTOs.Restaurant;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Restaurant;

/// <summary>Validates only OWN-15 fields included in a partial update.</summary>
public sealed class UpdateMenuSubmissionItemRequestDtoValidator : AbstractValidator<UpdateMenuSubmissionItemRequestDto>
{
    private static readonly string[] DishForms = ["soup", "dry", "other"];

    /// <summary>Creates conditional validation rules for each patchable proposal field.</summary>
    public UpdateMenuSubmissionItemRequestDtoValidator()
    {
        RuleFor(request => request.Name)
            .NotEmpty().WithMessage("Tên món không được để trống.")
            .Must(value => value is null || value.Trim().Length <= 200)
            .WithMessage("Tên món không được vượt quá 200 ký tự.")
            .When(request => request.NameSpecified);
        RuleFor(request => request.Description)
            .NotEmpty().WithMessage("Mô tả món không được để trống.")
            .Must(value => value is not null && value.Trim().Length is >= 30 and <= 2000)
            .WithMessage("Mô tả món phải từ 30 đến 2000 ký tự.")
            .When(request => request.DescriptionSpecified);
        RuleFor(request => request.DishForm)
            .NotNull().WithMessage("Loại món không được để trống.")
            .Must(value => value is not null && DishForms.Contains(value, StringComparer.OrdinalIgnoreCase))
            .WithMessage("Loại món phải là soup, dry hoặc other.")
            .When(request => request.DishFormSpecified);
        RuleFor(request => request.Price)
            .NotNull().WithMessage("Giá món không được để trống.")
            .GreaterThanOrEqualTo(0).WithMessage("Giá món phải lớn hơn hoặc bằng 0.")
            .Must(BeValidVndAmount).WithMessage("Giá món phải là số nguyên VND có tối đa 12 chữ số.")
            .When(request => request.PriceSpecified);
        RuleFor(request => request.CoverMediaId)
            .GreaterThan(0).WithMessage("Mã ảnh món phải lớn hơn 0.")
            .When(request => request.CoverMediaIdSpecified && request.CoverMediaId.HasValue);
        RuleFor(request => request.TagIds)
            .NotNull().WithMessage("Danh sách tag không được là null.")
            .Must(ids => ids is not null && ids.All(id => id > 0) && ids.Distinct().Count() == ids.Count)
            .WithMessage("Danh sách tag phải gồm các mã dương và không trùng nhau.")
            .When(request => request.TagIdsSpecified);
        RuleFor(request => request.SortOrder)
            .NotNull().WithMessage("Thứ tự món không được là null.")
            .GreaterThanOrEqualTo((short)0).WithMessage("Thứ tự món phải lớn hơn hoặc bằng 0.")
            .When(request => request.SortOrderSpecified);
    }

    private static bool BeValidVndAmount(decimal? price) =>
        !price.HasValue ||
        price.Value == decimal.Truncate(price.Value) && price.Value <= 999_999_999_999m;
}
