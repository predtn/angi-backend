using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Media;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Media;
using ANGI.Application.DTOs.Media;
using ANGI.Domain.Entities;
using FluentValidation;

namespace ANGI.Application.UseCases.Media.Upload;

/// <summary>Implements MEDIA-01 by validating, uploading, and recording one media file.</summary>
public sealed class UploadMediaUseCase : IUploadMediaUseCase
{
    private const long ImageLimitBytes = 5 * 1024 * 1024;
    private const long PdfLimitBytes = 10 * 1024 * 1024;

    private readonly IValidator<UploadMediaRequestDto> _validator;
    private readonly ICloudinaryService _cloudinaryService;
    private readonly IMediaRepository _mediaRepository;
    private readonly ICurrentUserService _currentUserService;
    private readonly IUnitOfWork _unitOfWork;

    /// <summary>Initializes the upload workflow and its storage dependencies.</summary>
    public UploadMediaUseCase(
        IValidator<UploadMediaRequestDto> validator,
        ICloudinaryService cloudinaryService,
        IMediaRepository mediaRepository,
        ICurrentUserService currentUserService,
        IUnitOfWork unitOfWork)
    {
        _validator = validator;
        _cloudinaryService = cloudinaryService;
        _mediaRepository = mediaRepository;
        _currentUserService = currentUserService;
        _unitOfWork = unitOfWork;
    }

    /// <summary>Validates file policy, uploads the content, and persists its metadata.</summary>
    public async Task<MediaFileDto> ExecuteAsync(UploadMediaRequestDto request, CancellationToken ct)
    {
        // STEP-01: Validate the request.
        await _validator.ValidateAndThrowAsync(request, ct);

        // STEP-02: Prepare uploaderId, normalizedMimeType through request.MimeType.Trim().ToLowerInvariant, isPdf and isImage.
        var uploaderId = _currentUserService.UserId
            ?? throw new UnauthorizedException("UNAUTHORIZED", "Bạn cần đăng nhập để tải tệp lên.");

        var normalizedMimeType = request.MimeType.Trim().ToLowerInvariant();
        var isPdf = normalizedMimeType == "application/pdf";
        var isImage = normalizedMimeType is "image/jpeg" or "image/png" or "image/webp";
        // STEP-03: Reject with UNSUPPORTED_MEDIA_TYPE when isImage is false and isPdf && request.Purpose == "verification_doc" is false.
        if (!isImage && !(isPdf && request.Purpose == "verification_doc"))
        {
            throw new BadRequestException(
                "UNSUPPORTED_MEDIA_TYPE",
                "Định dạng tệp không được hỗ trợ cho mục đích này.");
        }

        // STEP-04: Prepare sizeLimit.
        var sizeLimit = isPdf ? PdfLimitBytes : ImageLimitBytes;
        // STEP-05: Reject with FILE_TOO_LARGE when request.SizeBytes > sizeLimit.
        if (request.SizeBytes > sizeLimit)
        {
            throw new BadRequestException(
                "FILE_TOO_LARGE",
                "Tệp vượt quá dung lượng cho phép.");
        }

        // STEP-06: Prepare isPrivate, then call cloudinaryService.UploadAsync and keep upload.
        var isPrivate = request.Purpose is "verification_doc" or "report_evidence";
        var upload = await _cloudinaryService.UploadAsync(
            request.Content!,
            request.FileName,
            normalizedMimeType,
            request.Purpose,
            isPrivate,
            ct);

        // STEP-07: Prepare mediaFile as MediaFile.
        var mediaFile = new MediaFile
        {
            UploaderId = uploaderId,
            StorageKey = upload.StorageKey,
            Url = upload.Url,
            MimeType = normalizedMimeType,
            SizeBytes = request.SizeBytes
        };

        // STEP-08: Queue mediaFile through mediaRepository.Add.
        _mediaRepository.Add(mediaFile);
        // STEP-09: Save the pending changes.
        await _unitOfWork.SaveChangesAsync(ct);

        // STEP-10: Return a new MediaFileDto.
        return new MediaFileDto
        {
            Id = mediaFile.Id,
            Url = isPrivate ? _cloudinaryService.GetUrl(mediaFile.StorageKey) : mediaFile.Url,
            MimeType = mediaFile.MimeType,
            SizeBytes = mediaFile.SizeBytes.GetValueOrDefault(),
            Purpose = request.Purpose,
            CreatedAt = mediaFile.CreatedAt
        };
    }
}
