using ANGI.Application.Common.Interfaces.Repositories.Moderation;
using ANGI.Application.Common.Interfaces.UseCases.Moderation;
using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Moderation;
using FluentValidation;

namespace ANGI.Application.UseCases.Moderation.GetModReviews;

/// <summary>Implements MOD-F1 review list query for moderation.</summary>
public sealed class GetModReviewsUseCase(
    IValidator<GetModReviewsRequestDto> validator,
    ModerationAccess access,
    IReviewModerationRepository repository) : IGetModReviewsUseCase
{
    public async Task<Paged<ModReviewDto>> ExecuteAsync(GetModReviewsRequestDto request, CancellationToken ct)
    {
        await validator.ValidateAndThrowAsync(request, ct);
        await access.RequireAsync(ct, "REVIEW_MODERATE");

        var (items, totalItems) = await repository.GetModReviewsAsync(request, ct);
        var totalPages = (int)Math.Ceiling((double)totalItems / request.PageSize);
        var mappedItems = items.Select(ModerationMapper.ModReview).ToArray();

        return new Paged<ModReviewDto>
        {
            Items = mappedItems,
            Page = request.Page,
            PageSize = request.PageSize,
            TotalItems = totalItems,
            TotalPages = totalPages
        };
    }
}
