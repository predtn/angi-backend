using ANGI.Application.Common.Interfaces.UseCases.Account;
using ANGI.Application.DTOs.Account;
using ANGI.WebApi.Common.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace ANGI.WebApi.Controllers.Account;

/// <summary>Exposes authenticated personal profile management endpoints (ME-01, ME-02).</summary>
[ApiController]
[Route("api/v1/me")]
[Authorize]
public sealed class MeController : ControllerBase
{
    private readonly IGetMyProfileUseCase _getMyProfileUseCase;
    private readonly IUpdateMyProfileUseCase _updateMyProfileUseCase;

    public MeController(
        IGetMyProfileUseCase getMyProfileUseCase,
        IUpdateMyProfileUseCase updateMyProfileUseCase)
    {
        _getMyProfileUseCase = getMyProfileUseCase;
        _updateMyProfileUseCase = updateMyProfileUseCase;
    }

    /// <summary>Lấy thông tin profile của người dùng đang đăng nhập (ME-01).</summary>
    /// <param name="ct">Mã hủy yêu cầu thao tác bất đồng bộ.</param>
    /// <response code="200">Lấy hồ sơ cá nhân thành công.</response>
    /// <response code="401">Chưa đăng nhập hoặc token không hợp lệ.</response>
    /// <response code="404">Không tìm thấy tài khoản người dùng.</response>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<UserProfileDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetProfile(CancellationToken ct)
    {
        var result = await _getMyProfileUseCase.ExecuteAsync(ct);
        return Ok(new ApiResponse<UserProfileDto>
        {
            Success = true,
            Data = result
        });
    }

    /// <summary>Cập nhật thông tin cá nhân của người dùng đang đăng nhập (ME-02).</summary>
    /// <param name="request">Payload chứa các trường cần cập nhật.</param>
    /// <param name="ct">Mã hủy yêu cầu thao tác bất đồng bộ.</param>
    /// <response code="200">Cập nhật thông tin cá nhân thành công.</response>
    /// <response code="400">Dữ liệu gửi lên không hợp lệ (VALIDATION_FAILED).</response>
    /// <response code="401">Chưa đăng nhập hoặc token không hợp lệ.</response>
    /// <response code="404">Không tìm thấy tài khoản hoặc không tìm thấy ảnh đại diện (MEDIA_NOT_FOUND).</response>
    [HttpPatch]
    [ProducesResponseType(typeof(ApiResponse<UserProfileDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateProfile(
        [FromBody, BindRequired] UpdateProfileRequestDto request,
        CancellationToken ct)
    {
        var result = await _updateMyProfileUseCase.ExecuteAsync(request, ct);
        return Ok(new ApiResponse<UserProfileDto>
        {
            Success = true,
            Data = result
        });
    }
}
