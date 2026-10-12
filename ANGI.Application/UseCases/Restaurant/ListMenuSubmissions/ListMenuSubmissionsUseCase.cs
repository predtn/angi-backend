using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Restaurant;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Restaurant;
using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Restaurant;
using ANGI.Domain.Enums;
using FluentValidation;

namespace ANGI.Application.UseCases.Restaurant.ListMenuSubmissions;

/// <summary>Implements OWN-12 with status filtering and page metadata.</summary>
public sealed class ListMenuSubmissionsUseCase : IListMenuSubmissionsUseCase
{
    private readonly IValidator<ListMenuSubmissionsRequestDto> _validator;
    private readonly IRestaurantMenuRepository _repository;
    private readonly ICurrentUserService _currentUser;

    /// <summary>Initializes submission-history validation and query dependencies.</summary>
    public ListMenuSubmissionsUseCase(
        IValidator<ListMenuSubmissionsRequestDto> validator,
        IRestaurantMenuRepository repository,
        ICurrentUserService currentUser)
    {
        _validator = validator;
        _repository = repository;
        _currentUser = currentUser;
    }

    /// <summary>Returns the requested owner submission page in newest-first order.</summary>
    public async Task<Paged<MenuSubmissionDto>> ExecuteAsync(
        ListMenuSubmissionsRequestDto request,
        CancellationToken ct)
    {
        await _validator.ValidateAndThrowAsync(request, ct);

        var ownerId = _currentUser.UserId
            ?? throw new UnauthorizedException("UNAUTHORIZED", "Bạn cần đăng nhập để xem lịch sử menu.");
        MenuSubmissionStatus? status = request.Status is null
            ? null
            : Enum.Parse<MenuSubmissionStatus>(request.Status, true);
        var result = await _repository.GetSubmissionsAsync(
            ownerId,
            status,
            request.Page,
            request.PageSize,
            ct);

        return new Paged<MenuSubmissionDto>
        {
            Items = result.Items.Select(RestaurantMenuMapper.MapSubmission).ToArray(),
            Page = request.Page,
            PageSize = request.PageSize,
            TotalItems = result.TotalItems,
            TotalPages = (int)Math.Ceiling(result.TotalItems / (double)request.PageSize)
        };
    }
}
