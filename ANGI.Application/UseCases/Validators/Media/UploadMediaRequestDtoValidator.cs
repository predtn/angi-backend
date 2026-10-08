using ANGI.Application.DTOs.Media;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Media;

/// <summary>Validates fields common to every MEDIA-01 upload before storage rules run.</summary>
public sealed class UploadMediaRequestDtoValidator : AbstractValidator<UploadMediaRequestDto>
{
    private static readonly string[] Purposes =
    [
        "avatar", "restaurant", "dish", "blog", "review", "verification_doc", "report_evidence"
    ];

    /// <summary>Creates the validation rules for an upload request.</summary>
    public UploadMediaRequestDtoValidator()
    {
        RuleFor(x => x.Content).NotNull();
        RuleFor(x => x.FileName).NotEmpty();
        RuleFor(x => x.MimeType).NotEmpty();
        RuleFor(x => x.SizeBytes).GreaterThan(0);
        RuleFor(x => x.Purpose)
            .NotEmpty()
            .Must(purpose => Purposes.Contains(purpose, StringComparer.Ordinal))
            .WithMessage("Purpose is not supported.");
    }
}
