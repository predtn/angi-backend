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
        await _validator.ValidateAndThrowAsync(request, ct);

        var ownerId = _currentUser.UserId
            ?? throw new UnauthorizedException("UNAUTHORIZED", "Bạn cần đăng nhập để thêm món.");
        await using var transaction = await _unitOfWork.BeginTransactionAsync(ct);
        var submission = await _repository.LockOwnedSubmissionAsync(ownerId, submissionId, ct)
            ?? throw new NotFoundException("SUBMISSION_NOT_FOUND", "Không tìm thấy bản menu.");
        RestaurantMenuRules.EnsureEditable(submission);

        var tagIds = request.TagIds?.ToArray() ?? Array.Empty<short>();
        await RestaurantMenuRules.ValidateTagIdsAsync(_repository, tagIds, ct);
        var coverMedia = await RestaurantMenuRules.GetOwnedMediaAsync(
            _mediaRepository,
            ownerId,
            request.CoverMediaId,
            ct);
        var nextSortOrder = request.SortOrder ?? (submission.Items.Count == 0
            ? (short)0
            : (short)(submission.Items.Max(item => item.SortOrder) + 1));
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

        _repository.AddItem(item);
        await _unitOfWork.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return RestaurantMenuMapper.MapItem(item, "new", coverMedia?.Url);
    }
}
