using ANGI.Application.Common.Interfaces.Repositories.Moderation;
using ANGI.Application.Common.Interfaces.UseCases.Moderation;
using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Moderation;
using FluentValidation;

namespace ANGI.Application.UseCases.Moderation.ListModRestaurants;

/// <summary>Implements MOD-R1 with either restaurant moderation or verification permission.</summary>
public sealed class ListModRestaurantsUseCase(IValidator<ListModRestaurantsRequestDto> validator,
    ModerationAccess access, IRestaurantModerationRepository repository, TimeProvider clock) : IListModRestaurantsUseCase
{
    /// <summary>Validates filters, authorizes the caller, and maps the requested restaurant page.</summary>
    public async Task<Paged<ModRestaurantCardDto>> ExecuteAsync(ListModRestaurantsRequestDto request, CancellationToken ct)
    {
        await validator.ValidateAndThrowAsync(request, ct);
        await access.RequireAsync(ct, "RESTAURANT_MODERATE", "RESTAURANT_VERIFY");
        var page = await repository.ListAsync(request, ct);
        return new Paged<ModRestaurantCardDto>
        {
            Items = page.Items.Select(item => ModerationMapper.Card(item, clock)).ToArray(),
            Page = page.Page, PageSize = page.PageSize, TotalItems = page.TotalItems, TotalPages = page.TotalPages
        };
    }
}
