using ANGI.Application.DTOs.Media;

namespace ANGI.Application.Common.Interfaces.UseCases.Media;

/// <summary>Uploads and records a media file for the authenticated user.</summary>
public interface IUploadMediaUseCase
{
    /// <summary>Validates, uploads, persists, and returns the uploaded media metadata.</summary>
    Task<MediaFileDto> ExecuteAsync(UploadMediaRequestDto request, CancellationToken ct);
}
