using ANGI.Application.Common.Interfaces.UseCases.Administration;
using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Administration;
using ANGI.Application.DTOs.Moderation;
using ANGI.WebApi.Common.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ANGI.WebApi.Controllers.Administration;

/// <summary>Exposes system-wide audit log queries for administrators.</summary>
[Authorize(Roles = "ADMIN")]
[ApiController]
[Route("api/v1/admin/audit-logs")]
public sealed class AdminAuditLogsController(
    IGetSystemAuditLogsUseCase getSystemAuditLogs,
    IGetSystemAuditLogDetailUseCase getSystemAuditLogDetail) : ControllerBase
{
    // ADM-A1
    /// <summary>Lists system audit logs, newest first, with optional filters and pagination.</summary>
    /// <param name="request">Contains actorId, subjectUserId, actorRole, action, entityType, entityId, from, to, page and pageSize.</param>
    /// <param name="ct">Cancels the asynchronous request.</param>
    /// <response code="200">The paginated audit logs were returned.</response>
    /// <response code="400">The query parameters failed validation (VALIDATION_FAILED).</response>
    /// <response code="401">The access token is missing or invalid.</response>
    /// <response code="403">The caller is not an administrator.</response>
    /// <response code="404">The route does not exist.</response>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<Paged<AuditLogDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAuditLogs([FromQuery] GetSystemAuditLogsRequestDto request, CancellationToken ct)
    {
        var result = await getSystemAuditLogs.ExecuteAsync(request, ct);
        return Ok(new ApiResponse<Paged<AuditLogDto>> { Success = true, Data = result });
    }

    // ADM-A2
    /// <summary>Gets one audit log record including old and new values.</summary>
    /// <param name="auditLogId">Identifies the audit log record.</param>
    /// <param name="ct">Cancels the asynchronous request.</param>
    /// <response code="200">The audit log was returned.</response>
    /// <response code="400">The auditLogId is not a valid number (VALIDATION_FAILED).</response>
    /// <response code="401">The access token is missing or invalid.</response>
    /// <response code="403">The caller is not an administrator.</response>
    /// <response code="404">The audit log does not exist (AUDIT_LOG_NOT_FOUND).</response>
    [HttpGet("{auditLogId:long}")]
    [ProducesResponseType(typeof(ApiResponse<AuditLogDetailDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAuditLogDetail(long auditLogId, CancellationToken ct)
    {
        var result = await getSystemAuditLogDetail.ExecuteAsync(auditLogId, ct);
        return Ok(new ApiResponse<AuditLogDetailDto> { Success = true, Data = result });
    }
}
