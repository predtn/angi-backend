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
        RuleFor(x => x.Content).NotNull().WithMessage("Tệp là bắt buộc.");
        RuleFor(x => x.FileName).NotEmpty().WithMessage("Tên tệp là bắt buộc.");
        RuleFor(x => x.MimeType).NotEmpty().WithMessage("Không xác định được loại tệp.");
        RuleFor(x => x.SizeBytes).GreaterThan(0).WithMessage("Tệp không được rỗng.");
        RuleFor(x => x.Purpose)
            .NotEmpty().WithMessage("Mục đích tải lên là bắt buộc.")
            .Must(purpose => Purposes.Contains(purpose, StringComparer.Ordinal))
            .WithMessage("Mục đích tải lên không hợp lệ.");
    }
}
