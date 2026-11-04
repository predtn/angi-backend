using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Moderation;
using ANGI.Application.Common.Interfaces.UseCases.Moderation;
using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Moderation;
using FluentValidation;

namespace ANGI.Application.UseCases.Moderation.GetUserAuditLogs;

/// <summary>Implements MOD-U5 (F-55) to view paginated audit logs associated with a user.</summary>
public sealed class GetUserAuditLogsUseCase(
    IValidator<GetUserAuditLogsRequestDto> validator,
    ModerationAccess access,
    IUserModerationRepository repository) : IGetUserAuditLogsUseCase
{
    /// <inheritdoc />
    public async Task<Paged<AuditLogDto>> ExecuteAsync(int userId, GetUserAuditLogsRequestDto request, CancellationToken ct)
    {
        await validator.ValidateAndThrowAsync(request, ct);
        await access.RequireAsync(ct, "USER_VIEW_AUDIT");

        if (userId <= 0 || !await repository.UserExistsAsync(userId, ct))
        {
            throw new NotFoundException("USER_NOT_FOUND", "Không tìm thấy người dùng.");
        }

        return await repository.GetUserAuditLogsAsync(userId, request, ct);
    }
}
