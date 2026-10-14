using ANGI.Application.DTOs.Restaurant;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Restaurant;

/// <summary>Validates one document reference in an OWN-06 request.</summary>
public sealed class VerificationDocumentRequestDtoValidator : AbstractValidator<VerificationDocumentRequestDto>
{
    private static readonly IReadOnlySet<string> AllowedDocumentTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "business_license",
        "food_safety_cert",
        "id_card",
        "storefront_photo",
        "other"
    };

    /// <summary>Creates the documented document-type and media-id rules.</summary>
    public VerificationDocumentRequestDtoValidator()
    {
        RuleFor(document => document.DocType)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("Loại giấy tờ là bắt buộc.")
            .Must(documentType => documentType is not null && AllowedDocumentTypes.Contains(documentType))
            .WithMessage("Loại giấy tờ không hợp lệ.");

        RuleFor(document => document.MediaId)
            .GreaterThan(0).WithMessage("Mã tệp phải lớn hơn 0.");
    }
}
