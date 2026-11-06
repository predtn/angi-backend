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
        // STEP-01: Validate the request.
        await validator.ValidateAndThrowAsync(request, ct);
        // STEP-02: Check the caller's permission through MenuPermissionGuard.RequireAsync.
        await MenuPermissionGuard.RequireAsync(currentUser, repository, ct);
        // STEP-03: Load (items, total) through repository.ListAsync.
        var (items, total) = await repository.ListAsync(request, ct);
        // STEP-04: Return a new Paged<ModMenuSubmissionCardDto>.
        return new Paged<ModMenuSubmissionCardDto>
        {
            Items = items.Select(MenuReviewMapper.Card).ToArray(),
            Page = request.Page, PageSize = request.PageSize, TotalItems = total,
            TotalPages = (int)Math.Ceiling((double)total / request.PageSize)
        };
    }
}
