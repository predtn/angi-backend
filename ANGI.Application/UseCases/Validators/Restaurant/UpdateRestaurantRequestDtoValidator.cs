using ANGI.Application.DTOs.Restaurant;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Restaurant;

/// <summary>Validates only the OWN-03 fields present in a partial update.</summary>
public sealed class UpdateRestaurantRequestDtoValidator : AbstractValidator<UpdateRestaurantRequestDto>
{
    /// <summary>Creates validation rules matching the nullable and non-nullable PATCH fields.</summary>
    public UpdateRestaurantRequestDtoValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Tên nhà hàng không được để trống.")
            .MaximumLength(200).WithMessage("Tên nhà hàng không được vượt quá 200 ký tự.")
            .When(x => x.NameSpecified);
        RuleFor(x => x.Description)
            .MaximumLength(2000).WithMessage("Mô tả không được vượt quá 2000 ký tự.")
            .When(x => x.DescriptionSpecified && x.Description is not null);
        RuleFor(x => x.Phone)
            .NotEmpty().WithMessage("Số điện thoại không được để trống.")
            .MaximumLength(20).WithMessage("Số điện thoại không được vượt quá 20 ký tự.")
            .When(x => x.PhoneSpecified);
        RuleFor(x => x.Email)
            .Must(BeValidEmail).WithMessage("Email không đúng định dạng.")
            .When(x => x.EmailSpecified && !string.IsNullOrWhiteSpace(x.Email));
        RuleFor(x => x.Website)
            .Must(BeHttpUrl).WithMessage("Website phải là URL http hoặc https.")
            .When(x => x.WebsiteSpecified && !string.IsNullOrWhiteSpace(x.Website));
        RuleFor(x => x.AddressLine)
            .NotEmpty().WithMessage("Địa chỉ không được để trống.")
            .MaximumLength(255).WithMessage("Địa chỉ không được vượt quá 255 ký tự.")
            .When(x => x.AddressLineSpecified);
        RuleFor(x => x.Ward)
            .MaximumLength(100).WithMessage("Phường/xã không được vượt quá 100 ký tự.")
            .When(x => x.WardSpecified && x.Ward is not null);
        RuleFor(x => x.District)
            .NotEmpty().WithMessage("Quận/huyện không được để trống.")
            .MaximumLength(100).WithMessage("Quận/huyện không được vượt quá 100 ký tự.")
            .When(x => x.DistrictSpecified);
        RuleFor(x => x.ProvinceName)
            .NotEmpty().WithMessage("Tỉnh/thành không được để trống.")
            .MaximumLength(100).WithMessage("Tỉnh/thành không được vượt quá 100 ký tự.")
            .When(x => x.ProvinceNameSpecified);
        RuleFor(x => x.Latitude)
            .NotNull().WithMessage("Vĩ độ không được là null.")
            .InclusiveBetween(-90m, 90m).WithMessage("Vĩ độ phải nằm trong khoảng -90 đến 90.")
            .When(x => x.LatitudeSpecified);
        RuleFor(x => x.Longitude)
            .NotNull().WithMessage("Kinh độ không được là null.")
            .InclusiveBetween(-180m, 180m).WithMessage("Kinh độ phải nằm trong khoảng -180 đến 180.")
            .When(x => x.LongitudeSpecified);
        RuleFor(x => x.PriceLevel).InclusiveBetween((short)1, (short)4)
            .WithMessage("Mức giá phải từ 1 đến 4.")
            .When(x => x.PriceLevelSpecified && x.PriceLevel.HasValue);
        RuleFor(x => x.CoverMediaId)
            .GreaterThan(0).WithMessage("Mã ảnh bìa phải lớn hơn 0.")
            .When(x => x.CoverMediaIdSpecified && x.CoverMediaId.HasValue);
        RuleFor(x => x.ImageMediaIds)
            .Cascade(CascadeMode.Stop)
            .NotNull().WithMessage("Danh sách ảnh không được là null.")
            .Must(ids => ids is not null && ids.Count <= 10).WithMessage("Chỉ được chọn tối đa 10 ảnh.")
            .Must(ids => ids is not null && ids.All(id => id > 0)).WithMessage("Mã ảnh phải lớn hơn 0.")
            .Must(ids => ids is not null && ids.Distinct().Count() == ids.Count).WithMessage("Danh sách ảnh không được trùng nhau.")
            .When(x => x.ImageMediaIdsSpecified);
    }

    /// <summary>Checks whether a supplied website is an absolute HTTP or HTTPS URL.</summary>
    private static bool BeHttpUrl(string? value) =>
        Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>Checks a trimmed address with the same practical shape required by the API.</summary>
    private static bool BeValidEmail(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return false;
        }

        var separatorIndex = trimmed.IndexOf('@');
        return !trimmed.Any(char.IsWhiteSpace) &&
               separatorIndex > 0 &&
               separatorIndex == trimmed.LastIndexOf('@') &&
               separatorIndex < trimmed.Length - 1;
    }
}
