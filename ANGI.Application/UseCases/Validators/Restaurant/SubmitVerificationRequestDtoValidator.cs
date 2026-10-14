using ANGI.Application.DTOs.Restaurant;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Restaurant;

/// <summary>Validates the legal information and required documents for OWN-06.</summary>
public sealed class SubmitVerificationRequestDtoValidator : AbstractValidator<SubmitVerificationRequestDto>
{
    /// <summary>Creates all request-boundary rules documented for verification submission.</summary>
    public SubmitVerificationRequestDtoValidator()
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
            .NotNull().WithMessage("Danh sách giấy tờ là bắt buộc.")
            .NotEmpty().WithMessage("Phải cung cấp ít nhất một giấy tờ.")
            .Must(documents => documents is not null &&
                documents.Any(document => document is not null && document.DocType == "business_license"))
            .WithMessage("Phải cung cấp ít nhất một giấy phép kinh doanh.");

        RuleForEach(request => request.Documents)
            .NotNull().WithMessage("Giấy tờ không được để trống.")
            .SetValidator(new VerificationDocumentRequestDtoValidator());
    }
}
