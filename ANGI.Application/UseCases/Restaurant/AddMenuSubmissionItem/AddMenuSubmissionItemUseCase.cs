using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Media;
using ANGI.Application.Common.Interfaces.Repositories.Restaurant;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Restaurant;
using ANGI.Application.DTOs.Restaurant;
using ANGI.Domain.Entities;
using FluentValidation;

namespace ANGI.Application.UseCases.Restaurant.AddMenuSubmissionItem;

/// <summary>Implements OWN-14 by adding a new proposed dish to a draft.</summary>
public sealed class AddMenuSubmissionItemUseCase : IAddMenuSubmissionItemUseCase
{
    private readonly IValidator<AddMenuSubmissionItemRequestDto> _validator;
    private readonly IRestaurantMenuRepository _repository;
    private readonly IMediaRepository _mediaRepository;
    private readonly ICurrentUserService _currentUser;
    private readonly IUnitOfWork _unitOfWork;

    /// <summary>Initializes proposal validation, ownership, and persistence dependencies.</summary>
    public AddMenuSubmissionItemUseCase(
        IValidator<AddMenuSubmissionItemRequestDto> validator,
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

    /// <summary>Validates catalog references and appends a normalized new-dish proposal.</summary>
    public async Task<MenuSubmissionItemDto> ExecuteAsync(
        long submissionId,
        AddMenuSubmissionItemRequestDto request,
        CancellationToken ct)
    {
        // STEP-01: Validate the request.
        await _validator.ValidateAndThrowAsync(request, ct);

        // STEP-02: Prepare ownerId.
        var ownerId = _currentUser.UserId
            ?? throw new UnauthorizedException("UNAUTHORIZED", "Bạn cần đăng nhập để thêm món.");
        // STEP-03: Open the database transaction.
        await using var transaction = await _unitOfWork.BeginTransactionAsync(ct);
        // STEP-04: Prepare submission from repository.LockOwnedSubmissionAsync.
        var submission = await _repository.LockOwnedSubmissionAsync(ownerId, submissionId, ct)
            ?? throw new NotFoundException("SUBMISSION_NOT_FOUND", "Không tìm thấy bản menu.");
        // STEP-05: Check the editable rule through RestaurantMenuRules.EnsureEditable.
        RestaurantMenuRules.EnsureEditable(submission);

        // STEP-06: Prepare tagIds from Array.Empty.
        var tagIds = request.TagIds?.ToArray() ?? Array.Empty<short>();
        // STEP-07: Run RestaurantMenuRules.ValidateTagIdsAsync.
        await RestaurantMenuRules.ValidateTagIdsAsync(_repository, tagIds, ct);
        // STEP-08: Load coverMedia through RestaurantMenuRules.GetOwnedMediaAsync.
        var coverMedia = await RestaurantMenuRules.GetOwnedMediaAsync(
            _mediaRepository,
            ownerId,
            request.CoverMediaId,
            ct);
        // STEP-09: Prepare highestSortOrder from submission.Items.Max, nextSortOrder and item as MenuSubmissionItem.
        var highestSortOrder = submission.Items.Count == 0
            ? (short)-1
            : submission.Items.Max(item => item.SortOrder);
        var nextSortOrder = request.SortOrder ?? (highestSortOrder == short.MaxValue
            ? short.MaxValue
            : (short)(highestSortOrder + 1));
        var item = new MenuSubmissionItem
        {
            SubmissionId = submission.Id,
            Submission = submission,
            Name = request.Name.Trim(),
            Description = request.Description.Trim(),
            DishForm = RestaurantMenuRules.ParseDishForm(request.DishForm),
            Price = request.Price!.Value,
            CoverMediaId = request.CoverMediaId,
            TagIds = tagIds,
            SortOrder = nextSortOrder
        };

        // STEP-10: Queue item through repository.AddItem.
        _repository.AddItem(item);
        // STEP-11: Save the pending changes.
        await _unitOfWork.SaveChangesAsync(ct);
        // STEP-12: Commit the transaction.
        await transaction.CommitAsync(ct);
        // STEP-13: Return the result of RestaurantMenuMapper.MapItem.
        return RestaurantMenuMapper.MapItem(item, "new", coverMedia?.Url);
    }
}
