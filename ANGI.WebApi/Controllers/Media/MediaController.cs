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

    /// <summary>Upload ảnh hoặc tài liệu và trả về thông tin metadata (MEDIA-01).</summary>
    /// <param name="form">Biểu mẫu multipart/form-data chứa file và purpose.</param>
    /// <param name="ct">Mã hủy yêu cầu thao tác bất đồng bộ.</param>
    /// <response code="201">Tải lên tệp thành công.</response>
    /// <response code="400">Tệp vượt quá dung lượng cho phép hoặc định dạng không hỗ trợ.</response>
    /// <response code="401">Chưa đăng nhập hoặc token không hợp lệ.</response>
    [HttpPost]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(typeof(ApiResponse<MediaFileDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
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
