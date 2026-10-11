using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Media;
using ANGI.Application.Common.Interfaces.Repositories.Restaurant;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Restaurant;
using ANGI.Application.DTOs.Restaurant;
using FluentValidation;

namespace ANGI.Application.UseCases.Restaurant.UpdateMenuSubmissionItem;

/// <summary>Implements OWN-15 by applying supplied fields to one draft item.</summary>
public sealed class UpdateMenuSubmissionItemUseCase : IUpdateMenuSubmissionItemUseCase
{
    private readonly IValidator<UpdateMenuSubmissionItemRequestDto> _validator;
    private readonly IRestaurantMenuRepository _repository;
    private readonly IMediaRepository _mediaRepository;
    private readonly ICurrentUserService _currentUser;
    private readonly IUnitOfWork _unitOfWork;

    /// <summary>Initializes partial proposal update dependencies.</summary>
    public UpdateMenuSubmissionItemUseCase(
        IValidator<UpdateMenuSubmissionItemRequestDto> validator,
        IRestaurantMenuRepository repository,
        IMediaRepository mediaRepository,
        ICurrentUserService currentUser,
        IUnitOfWork unitOfWork)
    {
        _validator = validator;
        _repository = repository;
        _mediaRepository = mediaRepository;
        _currentUser = currentUser;
        _unitOfWork = unitOfWork;
    }

    /// <summary>Validates and persists only fields explicitly present in the patch body.</summary>
    public async Task<MenuSubmissionItemDto> ExecuteAsync(
        long submissionId,
        long itemId,
        UpdateMenuSubmissionItemRequestDto request,
        CancellationToken ct)
    {
        await _validator.ValidateAndThrowAsync(request, ct);

        var ownerId = _currentUser.UserId
            ?? throw new UnauthorizedException("UNAUTHORIZED", "Bạn cần đăng nhập để sửa món.");
        await using var transaction = await _unitOfWork.BeginTransactionAsync(ct);
        var submission = await _repository.LockOwnedSubmissionAsync(ownerId, submissionId, ct)
            ?? throw new NotFoundException("SUBMISSION_ITEM_NOT_FOUND", "Không tìm thấy món trong bản menu.");
        RestaurantMenuRules.EnsureEditable(submission);
        var item = await _repository.GetTrackedItemAsync(ownerId, submissionId, itemId, ct)
            ?? throw new NotFoundException("SUBMISSION_ITEM_NOT_FOUND", "Không tìm thấy món trong bản menu.");

        string? replacementCoverUrl = null;

        if (request.NameSpecified) item.Name = request.Name!.Trim();
        if (request.DescriptionSpecified) item.Description = request.Description!.Trim();
        if (request.DishFormSpecified) item.DishForm = RestaurantMenuRules.ParseDishForm(request.DishForm!);
        if (request.PriceSpecified) item.Price = request.Price!.Value;
        if (request.SortOrderSpecified) item.SortOrder = request.SortOrder!.Value;
        if (request.TagIdsSpecified)
        {
            var tagIds = request.TagIds!.ToArray();
            await RestaurantMenuRules.ValidateTagIdsAsync(_repository, tagIds, ct);
            item.TagIds = tagIds;
        }
        if (request.CoverMediaIdSpecified)
        {
            var coverMedia = await RestaurantMenuRules.GetOwnedMediaAsync(
                _mediaRepository,
                ownerId,
                request.CoverMediaId,
                ct);
            replacementCoverUrl = coverMedia?.Url;
            item.CoverMedia = null;
            item.CoverMediaId = request.CoverMediaId;
        }

        await _unitOfWork.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        if (request.CoverMediaIdSpecified)
        {
            return RestaurantMenuMapper.MapItem(item, replacementCoverUrl);
        }

        return RestaurantMenuMapper.MapItem(item);
    }
}
