using ANGI.Application.DTOs.Restaurant;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Restaurant;

/// <summary>Validates the replacement legal information and document set for OWN-08.</summary>
/// <remarks>
/// Unlike OWN-06 there is no business-license requirement: the API Design sample for OWN-08 resubmits with a
/// food_safety_cert alone, and asks only for at least one document.
/// </remarks>
public sealed class ResubmitVerificationRequestDtoValidator : AbstractValidator<ResubmitVerificationRequestDto>
{
    /// <summary>Creates all request-boundary rules documented for verification resubmission.</summary>
    public ResubmitVerificationRequestDtoValidator()
    {
        RuleFor(request => request.LegalName)
            .NotEmpty().WithMessage("Tên pháp lý là bắt buộc.")
            .MaximumLength(255).WithMessage("Tên pháp lý không được vượt quá 255 ký tự.");

        RuleFor(request => request.BusinessLicenseNo)
            .NotEmpty().WithMessage("Số giấy phép kinh doanh là bắt buộc.")
            .MaximumLength(50).WithMessage("Số giấy phép kinh doanh không được vượt quá 50 ký tự.");

        RuleFor(request => request.TaxCode)
            .MaximumLength(20).WithMessage("Mã số thuế không được vượt quá 20 ký tự.")
            .When(request => request.TaxCode is not null);

        RuleFor(request => request.OwnerNote)
            .MaximumLength(1000).WithMessage("Ghi chú không được vượt quá 1000 ký tự.")
            .When(request => request.OwnerNote is not null);

        RuleFor(request => request.Documents)
            .Cascade(CascadeMode.Stop)
            .NotNull().WithMessage("Danh sách giấy tờ là bắt buộc.")
            .NotEmpty().WithMessage("Phải cung cấp ít nhất một giấy tờ.");

        RuleForEach(request => request.Documents)
            .NotNull().WithMessage("Giấy tờ không được để trống.")
            .SetValidator(new VerificationDocumentRequestDtoValidator());
    }
}
