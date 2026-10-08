using ANGI.Application.Common.Interfaces.UseCases.Media;
using ANGI.Application.DTOs.Media;
using ANGI.WebApi.Common.Models;
using ANGI.WebApi.Models.Media;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ANGI.WebApi.Controllers.Media;

/// <summary>Exposes authenticated media upload operations.</summary>
[ApiController]
[Route("api/v1/media")]
[Authorize]
public sealed class MediaController : ControllerBase
{
    private readonly IUploadMediaUseCase _uploadMediaUseCase;

    /// <summary>Initializes the HTTP boundary with the media upload use case.</summary>
    public MediaController(IUploadMediaUseCase uploadMediaUseCase)
    {
        _uploadMediaUseCase = uploadMediaUseCase;
    }

    /// <summary>Receives MEDIA-01 multipart content and returns persisted media metadata.</summary>
    [HttpPost]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(typeof(ApiResponse<MediaFileDto>), StatusCodes.Status201Created)]
    public async Task<IActionResult> Upload([FromForm] UploadMediaForm form, CancellationToken ct)
    {
        await using var content = form.File.OpenReadStream();
        var result = await _uploadMediaUseCase.ExecuteAsync(new UploadMediaRequestDto
        {
            Content = content,
            FileName = form.File.FileName,
            MimeType = form.File.ContentType,
            SizeBytes = form.File.Length,
            Purpose = form.Purpose
        }, ct);

        return StatusCode(StatusCodes.Status201Created, new ApiResponse<MediaFileDto>
        {
            Success = true,
            Data = result
        });
    }
}
