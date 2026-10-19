using ANGI.Application.Common.Interfaces.UseCases.Notification;
using ANGI.Application.DTOs.Notification;
using ANGI.WebApi.Common.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ANGI.WebApi.Controllers.Notification;

/// <summary>Exposes notification endpoints for the authenticated user (NOTI-01, NOTI-02).</summary>
[Authorize]
[ApiController]
[Route("api/v1/me/notifications")]
public sealed class MeNotificationController : ControllerBase
{
    private readonly IGetNotificationsUseCase _getNotificationsUseCase;
    private readonly IGetUnreadNotificationCountUseCase _getUnreadNotificationCountUseCase;

    public MeNotificationController(
        IGetNotificationsUseCase getNotificationsUseCase,
        IGetUnreadNotificationCountUseCase getUnreadNotificationCountUseCase)
    {
        _getNotificationsUseCase = getNotificationsUseCase;
        _getUnreadNotificationCountUseCase = getUnreadNotificationCountUseCase;
    }

    // NOTI-01
    /// <summary>Lấy danh sách thông báo của người dùng đang đăng nhập (NOTI-01).</summary>
    /// <param name="request">Bộ lọc trạng thái đã đọc, loại thông báo và thông tin phân trang.</param>
    /// <param name="ct">Mã hủy yêu cầu thao tác bất đồng bộ.</param>
    /// <response code="200">Lấy danh sách thông báo thành công.</response>
    /// <response code="400">Tham số phân trang hoặc bộ lọc không hợp lệ (VALIDATION_FAILED).</response>
    /// <response code="401">Chưa đăng nhập hoặc token không hợp lệ (UNAUTHORIZED).</response>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<GetNotificationsResponseDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetNotifications(
        [FromQuery] GetNotificationsRequestDto request,
        CancellationToken ct)
    {
        var result = await _getNotificationsUseCase.ExecuteAsync(request, ct);
        return Ok(new ApiResponse<GetNotificationsResponseDto>
        {
            Success = true,
            Data = result
        });
    }

    // NOTI-02
    /// <summary>Lấy số lượng thông báo chưa đọc của người dùng đang đăng nhập (NOTI-02).</summary>
    /// <param name="ct">Mã hủy yêu cầu thao tác bất đồng bộ.</param>
    /// <response code="200">Lấy số lượng thông báo chưa đọc thành công.</response>
    /// <response code="401">Chưa đăng nhập hoặc token không hợp lệ (UNAUTHORIZED).</response>
    [HttpGet("unread-count")]
    [ProducesResponseType(typeof(ApiResponse<GetUnreadNotificationCountResponseDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetUnreadCount(CancellationToken ct)
    {
        var result = await _getUnreadNotificationCountUseCase.ExecuteAsync(ct);
        return Ok(new ApiResponse<GetUnreadNotificationCountResponseDto>
        {
            Success = true,
            Data = result
        });
    }
}
