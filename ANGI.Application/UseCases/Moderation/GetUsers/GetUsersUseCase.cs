using ANGI.Application.Common.Interfaces.Repositories.Moderation;
using ANGI.Application.Common.Interfaces.UseCases.Moderation;
using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Moderation;
using FluentValidation;

namespace ANGI.Application.UseCases.Moderation.GetUsers;

/// <summary>Implements MOD-U1 to list users with filtering and pagination for moderation.</summary>
public sealed class GetUsersUseCase(
    IValidator<ListUsersRequestDto> validator,
    ModerationAccess access,
    IUserModerationRepository repository) : IGetUsersUseCase
{
    public async Task<Paged<AdminUserCardDto>> ExecuteAsync(ListUsersRequestDto request, CancellationToken ct)
    {
        await validator.ValidateAndThrowAsync(request, ct);
        await access.RequireAsync(ct, "USER_SUSPEND", "USER_BAN", "USER_VIEW_AUDIT");
        return await repository.ListAsync(request, ct);
    }
}
