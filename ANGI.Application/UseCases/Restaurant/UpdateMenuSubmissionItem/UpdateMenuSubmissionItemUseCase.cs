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
        // STEP-01: Validate the request.
        await _validator.ValidateAndThrowAsync(request, ct);

        // STEP-02: Prepare ownerId.
        var ownerId = _currentUser.UserId
            ?? throw new UnauthorizedException("UNAUTHORIZED", "Bạn cần đăng nhập để sửa món.");
        // STEP-03: Open the database transaction.
        await using var transaction = await _unitOfWork.BeginTransactionAsync(ct);
        // STEP-04: Prepare submission from repository.LockOwnedSubmissionAsync.
        var submission = await _repository.LockOwnedSubmissionAsync(ownerId, submissionId, ct)
            ?? throw new NotFoundException("SUBMISSION_ITEM_NOT_FOUND", "Không tìm thấy món trong bản menu.");
        // STEP-05: Check the editable rule through RestaurantMenuRules.EnsureEditable.
        RestaurantMenuRules.EnsureEditable(submission);
        // STEP-06: Prepare item from repository.GetTrackedItemAsync and replacementCoverUrl.
        var item = await _repository.GetTrackedItemAsync(ownerId, submissionId, itemId, ct)
            ?? throw new NotFoundException("SUBMISSION_ITEM_NOT_FOUND", "Không tìm thấy món trong bản menu.");

        string? replacementCoverUrl = null;

        // STEP-07: When request.NameSpecified is true, set item.Name.
        if (request.NameSpecified) item.Name = request.Name!.Trim();
        // STEP-08: When request.DescriptionSpecified is true, set item.Description.
        if (request.DescriptionSpecified) item.Description = request.Description!.Trim();
        // STEP-09: When request.DishFormSpecified is true, set item.DishForm.
        if (request.DishFormSpecified) item.DishForm = RestaurantMenuRules.ParseDishForm(request.DishForm!);
        // STEP-10: When request.PriceSpecified is true, set item.Price.
        if (request.PriceSpecified) item.Price = request.Price!.Value;
        // STEP-11: When request.SortOrderSpecified is true, set item.SortOrder.
        if (request.SortOrderSpecified) item.SortOrder = request.SortOrder!.Value;
        // STEP-12: When request.TagIdsSpecified is true, prepare tagIds through request.TagIds.ToArray, call RestaurantMenuRules.ValidateTagIdsAsync and 1 more action(s).
        if (request.TagIdsSpecified)
        {
            var tagIds = request.TagIds!.ToArray();
            await RestaurantMenuRules.ValidateTagIdsAsync(_repository, tagIds, ct);
            item.TagIds = tagIds;
        }
        // STEP-13: When request.CoverMediaIdSpecified is true, run the branch below.
        if (request.CoverMediaIdSpecified)
        {
            // STEP-14: Load coverMedia through RestaurantMenuRules.GetOwnedMediaAsync.
            var coverMedia = await RestaurantMenuRules.GetOwnedMediaAsync(
                _mediaRepository,
                ownerId,
                request.CoverMediaId,
                ct);
            // STEP-15: Set replacementCoverUrl, item.CoverMedia and item.CoverMediaId.
            replacementCoverUrl = coverMedia?.Url;
            item.CoverMedia = null;
            item.CoverMediaId = request.CoverMediaId;
        }

        // STEP-16: Save the pending changes.
        await _unitOfWork.SaveChangesAsync(ct);
        // STEP-17: Commit the transaction.
        await transaction.CommitAsync(ct);
        // STEP-18: Return the result of RestaurantMenuMapper.MapItem early when request.CoverMediaIdSpecified is true.
        if (request.CoverMediaIdSpecified)
        {
            return RestaurantMenuMapper.MapItem(item, replacementCoverUrl);
        }

        // STEP-19: Return the result of RestaurantMenuMapper.MapItem.
        return RestaurantMenuMapper.MapItem(item);
    }
}
