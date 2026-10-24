using ANGI.Application.Common.Interfaces.Repositories.Moderation;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Moderation;
using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Moderation;
using FluentValidation;

namespace ANGI.Application.UseCases.Moderation.ListVerifications;

/// <summary>Implements MOD-V1 verification queue filtering and pagination.</summary>
public sealed class ListVerificationsUseCase : IListVerificationsUseCase
{
    private readonly IValidator<ListVerificationsRequestDto> _validator;
    private readonly IVerificationReviewRepository _repository;
    private readonly ICurrentUserService _currentUserService;
    private readonly ICloudinaryService _cloudinaryService;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes validation, authorization, persistence, media, and clock boundaries.</summary>
    public ListVerificationsUseCase(
        IValidator<ListVerificationsRequestDto> validator,
        IVerificationReviewRepository repository,
        ICurrentUserService currentUserService,
        ICloudinaryService cloudinaryService,
        TimeProvider timeProvider)
    {
        _validator = validator;
        _repository = repository;
        _currentUserService = currentUserService;
        _cloudinaryService = cloudinaryService;
        _timeProvider = timeProvider;
    }

    /// <summary>Validates MOD-V1, enforces RESTAURANT_VERIFY, and returns the oldest matching submissions first.</summary>
    public async Task<Paged<ModVerificationCardDto>> ExecuteAsync(
        ListVerificationsRequestDto request,
        CancellationToken ct)
    {
        await _validator.ValidateAndThrowAsync(request, ct);
        await ModerationPermissionGuard.RequireAsync(_currentUserService, _repository, ct);

        request.Status = request.Status.ToLowerInvariant();
        request.Province = string.IsNullOrWhiteSpace(request.Province) ? null : request.Province.Trim();
        var (items, totalItems) = await _repository.ListAsync(request, ct);

        return new Paged<ModVerificationCardDto>
        {
            Items = items.Select(item => ModerationVerificationMapper.MapCard(
                item,
                _cloudinaryService,
                _timeProvider)).ToArray(),
            Page = request.Page,
            PageSize = request.PageSize,
            TotalItems = totalItems,
            TotalPages = (int)Math.Ceiling((double)totalItems / request.PageSize)
        };
    }
}
