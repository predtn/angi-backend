using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Media;
using ANGI.Application.Common.Interfaces.Repositories.Restaurant;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.DTOs.Restaurant;
using ANGI.Application.UseCases.Restaurant.UpdateMenuSubmissionItem;
using ANGI.Application.UseCases.Validators.Restaurant;
using ANGI.Application.Common.Exceptions;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using FluentValidation;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Restaurant.UpdateMenuSubmissionItem;

/// <summary>Verifies UpdateMenuSubmissionItemUseCase patches an owned draft item and distinguishes omitted from cleared fields.</summary>
public sealed class UpdateMenuSubmissionItemUseCaseTests
{
    // TEST-01: Apply supplied fields and distinguish an explicit null cover from an omitted cover.
    /// <summary>Verifies OWN-15 partial-update semantics including cover removal.</summary>
    [Fact]
    public async Task ExecuteAsync_WithExplicitNullCover_ShouldUpdateFieldsAndRemoveCover()
    {
        // STEP-01: Create item as MenuSubmissionItem.
        var item = new MenuSubmissionItem
        {
            Id = 340,
            SubmissionId = 14,
            Submission = new MenuSubmission { Id = 14 },
            Name = "Tên cũ",
            Description = "Mô tả cũ đủ dài cho món ăn hiện tại.",
            CoverMediaId = 9100
        };
        // STEP-02: Mock IRestaurantMenuRepository, ICurrentUserService and 1 more; stub repository.LockOwnedSubmissionAsync, repository.GetTrackedItemAsync and 3 more.
        var repository = new Mock<IRestaurantMenuRepository>();
        repository.Setup(value => value.LockOwnedSubmissionAsync(12, 14, It.IsAny<CancellationToken>()))
            .ReturnsAsync(item.Submission);
        repository.Setup(value => value.GetTrackedItemAsync(12, 14, 340, It.IsAny<CancellationToken>()))
            .ReturnsAsync(item);
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(value => value.UserId).Returns(12);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(value => value.BeginTransactionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IUnitOfWorkTransaction>());
        unitOfWork.Setup(value => value.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
        // STEP-03: Create the UpdateMenuSubmissionItemUseCase under test as useCase.
        var useCase = new UpdateMenuSubmissionItemUseCase(
            new UpdateMenuSubmissionItemRequestDtoValidator(),
            repository.Object,
            Mock.Of<IMediaRepository>(),
            currentUser.Object,
            unitOfWork.Object);
        // STEP-04: Create request as UpdateMenuSubmissionItemRequestDto.
        var request = new UpdateMenuSubmissionItemRequestDto
        {
            Name = " Tên mới ",
            CoverMediaId = null
        };

        // STEP-05: Run useCase.ExecuteAsync with request and keep result.
        var result = await useCase.ExecuteAsync(14, 340, request, CancellationToken.None);

        // STEP-06: Assert item.Name is the expected text, item.CoverMediaId is null and result.CoverUrl is null.
        item.Name.Should().Be("Tên mới");
        item.CoverMediaId.Should().BeNull();
        result.CoverUrl.Should().BeNull();
        // STEP-07: Verify SaveChangesAsync is called once.
        unitOfWork.Verify(value => value.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-02: Reject updates for every non-draft submission status.
    /// <summary>Ensures a menu already sent for review cannot be changed.</summary>
    [Theory]
    [InlineData(MenuSubmissionStatus.Pending)]
    [InlineData(MenuSubmissionStatus.Approved)]
    [InlineData(MenuSubmissionStatus.Rejected)]
    [InlineData(MenuSubmissionStatus.Cancelled)]
    public async Task ExecuteAsync_WithNonDraftSubmission_ShouldThrowConflict(MenuSubmissionStatus status)
    {
        // STEP-01: Mock IRestaurantMenuRepository; stub repository.LockOwnedSubmissionAsync.
        var repository = new Mock<IRestaurantMenuRepository>();
        repository.Setup(value => value.LockOwnedSubmissionAsync(12, 14, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MenuSubmission { Id = 14, Status = status });

        // STEP-02: Wrap CreateUseCase().ExecuteAsync with an empty UpdateMenuSubmissionItemRequestDto in act so its outcome can be asserted.
        var act = () => CreateUseCase(repository, new Mock<IMediaRepository>()).ExecuteAsync(
            14, 340, new UpdateMenuSubmissionItemRequestDto(), CancellationToken.None);

        // STEP-03: Assert act throws ConflictException.
        await act.Should().ThrowAsync<ConflictException>();
    }

    // TEST-03: Hide missing and unowned submissions or items behind the not-found response.
    /// <summary>Ensures another owner's submission or item is indistinguishable from a missing one.</summary>
    [Fact]
    public async Task ExecuteAsync_WithUnknownSubmissionOrItem_ShouldThrowNotFound()
    {
        // STEP-01: Get repository from DraftRepository.
        var repository = DraftRepository(item: null);

        // STEP-02: Wrap CreateUseCase().ExecuteAsync with an empty UpdateMenuSubmissionItemRequestDto in act so its outcome can be asserted.
        var act = () => CreateUseCase(repository, new Mock<IMediaRepository>()).ExecuteAsync(
            14, 999, new UpdateMenuSubmissionItemRequestDto(), CancellationToken.None);

        // STEP-03: Assert act throws NotFoundException and exception.ErrorCode is "SUBMISSION_ITEM_NOT_FOUND".
        var exception = await act.Should().ThrowAsync<NotFoundException>();
        exception.Which.ErrorCode.Should().Be("SUBMISSION_ITEM_NOT_FOUND");
    }

    // TEST-04: Preserve every field when the PATCH body is empty.
    /// <summary>Ensures an empty PATCH changes nothing.</summary>
    [Fact]
    public async Task ExecuteAsync_WithEmptyPatch_ShouldKeepAllFieldsUnchanged()
    {
        // STEP-01: Get item from ExistingItem, derive before from CoverMediaId, item.SortOrder) and get repository from DraftRepository.
        var item = ExistingItem();
        var before = (item.Name, item.Description, item.DishForm, item.Price, item.CoverMediaId, item.SortOrder);
        var repository = DraftRepository(item);

        // STEP-02: Run CreateUseCase().ExecuteAsync with an empty UpdateMenuSubmissionItemRequestDto.
        await CreateUseCase(repository, new Mock<IMediaRepository>()).ExecuteAsync(
            14, 340, new UpdateMenuSubmissionItemRequestDto(), CancellationToken.None);

        // STEP-03: Assert Price, item.CoverMediaId, item.SortOrder) is before.
        (item.Name, item.Description, item.DishForm, item.Price, item.CoverMediaId, item.SortOrder)
            .Should().Be(before);
    }

    // TEST-05: Apply every supplied patch field and preserve the omitted cover field.
    /// <summary>Ensures supplied fields are applied and an omitted cover is kept.</summary>
    [Fact]
    public async Task ExecuteAsync_WithEveryPatchField_ShouldReplaceOnlySuppliedValues()
    {
        // STEP-01: Get item from ExistingItem and get repository from DraftRepository.
        var item = ExistingItem();
        var repository = DraftRepository(item);
        // STEP-02: Stub repository.GetExistingTagIdsAsync.
        repository.Setup(value => value.GetExistingTagIdsAsync(
                It.IsAny<IReadOnlyCollection<short>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([(short)3]);

        // STEP-03: Run CreateUseCase().ExecuteAsync with UpdateMenuSubmissionItemRequestDto { Name, Description, DishForm, ... }.
        await CreateUseCase(repository, new Mock<IMediaRepository>()).ExecuteAsync(
            14,
            340,
            new UpdateMenuSubmissionItemRequestDto
            {
                Name = " New name ",
                Description = $"  {new string('b', 30)}  ",
                DishForm = "soup",
                Price = 99,
                TagIds = [3],
                SortOrder = 7
            },
            CancellationToken.None);

        // STEP-04: Assert item.Name is "New name", item.Description is a new string, item.DishForm is DishForm.Soup, item.Price is 99 and 3 more.
        item.Name.Should().Be("New name");
        item.Description.Should().Be(new string('b', 30));
        item.DishForm.Should().Be(DishForm.Soup);
        item.Price.Should().Be(99);
        item.TagIds.Should().Equal((short)3);
        item.SortOrder.Should().Be(7);
        item.CoverMediaId.Should().Be(50);
    }

    // TEST-06: Reject PDF and non-dish-purpose media as a replacement cover.
    /// <summary>Ensures only a dish image can become a dish cover.</summary>
    [Theory]
    [InlineData("application/pdf", "raw/authenticated/verification_doc/file.pdf")]
    [InlineData("image/jpeg", "image/upload/verification_doc/file.jpg")]
    public async Task ExecuteAsync_WithInvalidCoverPurposeOrMime_ShouldReject(
        string mime,
        string storageKey)
    {
        // STEP-01: Get repository from DraftRepository.
        var repository = DraftRepository(ExistingItem());
        // STEP-02: Mock IMediaRepository; stub mediaRepository.GetOwnedByIdsAsync.
        var mediaRepository = new Mock<IMediaRepository>();
        mediaRepository.Setup(value => value.GetOwnedByIdsAsync(
                12, It.IsAny<IReadOnlyCollection<long>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MediaFile { Id = 60, MimeType = mime, StorageKey = storageKey }]);

        // STEP-03: Wrap CreateUseCase().ExecuteAsync with UpdateMenuSubmissionItemRequestDto { CoverMediaId = 60 } in act so its outcome can be asserted.
        var act = () => CreateUseCase(repository, mediaRepository).ExecuteAsync(
            14, 340, new UpdateMenuSubmissionItemRequestDto { CoverMediaId = 60 }, CancellationToken.None);

        // STEP-04: Assert act throws ValidationException.
        await act.Should().ThrowAsync<ValidationException>();
    }

    /// <summary>Creates draft item 340 of submission 14 with every field set.</summary>
    private static MenuSubmissionItem ExistingItem() => new()
    {
        Id = 340,
        SubmissionId = 14,
        Submission = new MenuSubmission { Id = 14 },
        Name = "Old name",
        Description = new string('a', 30),
        DishForm = DishForm.Dry,
        Price = 40,
        CoverMediaId = 50,
        TagIds = [(short)2],
        SortOrder = 1
    };

    /// <summary>Creates a repository mock that returns draft submission 14 and the given tracked item for owner 12.</summary>
    private static Mock<IRestaurantMenuRepository> DraftRepository(MenuSubmissionItem? item)
    {
        var repository = new Mock<IRestaurantMenuRepository>();
        var submission = item?.Submission ?? new MenuSubmission { Id = 14 };
        repository.Setup(value => value.LockOwnedSubmissionAsync(12, 14, It.IsAny<CancellationToken>()))
            .ReturnsAsync(submission);
        repository.Setup(value => value.GetTrackedItemAsync(12, 14, It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(item);
        return repository;
    }

    /// <summary>Creates the use case for owner 12 with the real validator and the given repositories.</summary>
    private static UpdateMenuSubmissionItemUseCase CreateUseCase(
        Mock<IRestaurantMenuRepository> repository,
        Mock<IMediaRepository> mediaRepository)
    {
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(value => value.UserId).Returns(12);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(value => value.BeginTransactionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IUnitOfWorkTransaction>());
        return new UpdateMenuSubmissionItemUseCase(
            new UpdateMenuSubmissionItemRequestDtoValidator(),
            repository.Object,
            mediaRepository.Object,
            currentUser.Object,
            unitOfWork.Object);
    }
}
