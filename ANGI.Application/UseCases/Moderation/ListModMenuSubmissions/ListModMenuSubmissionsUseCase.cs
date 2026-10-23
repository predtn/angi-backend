using ANGI.Application.Common.Interfaces.Repositories.Moderation;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Moderation;
using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Moderation;
using FluentValidation;

namespace ANGI.Application.UseCases.Moderation.ListModMenuSubmissions;

/// <summary>Implements the MOD-M1 permission-protected moderation queue.</summary>
public sealed class ListModMenuSubmissionsUseCase(
    IMenuReviewRepository repository, ICurrentUserService currentUser,
    IValidator<ListModMenuSubmissionsRequestDto> validator) : IListModMenuSubmissionsUseCase
{
    /// <summary>Validates queue filters and returns the requested page with change counts.</summary>
    public async Task<Paged<ModMenuSubmissionCardDto>> ExecuteAsync(ListModMenuSubmissionsRequestDto request, CancellationToken ct)
    {
        await validator.ValidateAndThrowAsync(request, ct);
        await MenuPermissionGuard.RequireAsync(currentUser, repository, ct);
        var (items, total) = await repository.ListAsync(request, ct);
        return new Paged<ModMenuSubmissionCardDto>
        {
            Items = items.Select(MenuReviewMapper.Card).ToArray(),
            Page = request.Page, PageSize = request.PageSize, TotalItems = total,
            TotalPages = (int)Math.Ceiling((double)total / request.PageSize)
        };
    }
}
