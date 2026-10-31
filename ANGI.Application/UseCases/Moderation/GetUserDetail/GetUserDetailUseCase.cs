using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Moderation;
using ANGI.Application.Common.Interfaces.UseCases.Moderation;
using ANGI.Application.DTOs.Moderation;

namespace ANGI.Application.UseCases.Moderation.GetUserDetail;

/// <summary>Implements MOD-U2 to retrieve complete user details for moderation.</summary>
public sealed class GetUserDetailUseCase(
    ModerationAccess access,
    IUserModerationRepository repository) : IGetUserDetailUseCase
{
    public async Task<ModUserDetailDto> ExecuteAsync(int userId, CancellationToken ct)
    {
        await access.RequireAsync(ct, "USER_SUSPEND", "USER_BAN", "USER_VIEW_AUDIT");
        if (userId <= 0)
        {
            throw new NotFoundException("USER_NOT_FOUND", "Không tìm thấy người dùng.");
        }

        var detail = await repository.GetDetailAsync(userId, ct);
        if (detail is null)
        {
            throw new NotFoundException("USER_NOT_FOUND", "Không tìm thấy người dùng.");
        }

        return detail;
    }
}
