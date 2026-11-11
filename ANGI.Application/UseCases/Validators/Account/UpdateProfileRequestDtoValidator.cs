using ANGI.Application.DTOs.Account;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Account;

/// <summary>Validates partial profile update fields for ME-02.</summary>
public sealed class UpdateProfileRequestDtoValidator : AbstractValidator<UpdateProfileRequestDto>
{
    public UpdateProfileRequestDtoValidator()
    {
        When(x => x.DisplayNameSpecified, () =>
        {
            RuleFor(x => x.DisplayName)
                .NotEmpty().WithMessage("Tên hiển thị không được để trống.")
                .Length(2, 100).WithMessage("Tên hiển thị phải từ 2 đến 100 ký tự.");
        });

        When(x => x.PhoneSpecified && x.Phone is not null, () =>
        {
            RuleFor(x => x.Phone)
                .MaximumLength(20).WithMessage("Số điện thoại không được vượt quá 20 ký tự.");
        });

        When(x => x.BioSpecified && x.Bio is not null, () =>
        {
            RuleFor(x => x.Bio)
                .MaximumLength(500).WithMessage("Tiểu sử không được vượt quá 500 ký tự.");
        });

        When(x => x.AvatarMediaIdSpecified && x.AvatarMediaId is not null, () =>
        {
            RuleFor(x => x.AvatarMediaId)
                .GreaterThan(0).WithMessage("Mã ảnh đại diện không hợp lệ.");
        });
    }
}
