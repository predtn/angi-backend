using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Media;
using ANGI.Application.Common.Interfaces.Repositories.Restaurant;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.DTOs.Restaurant;
using ANGI.Application.UseCases.Restaurant.AddMenuSubmissionItem;
using ANGI.Application.UseCases.Validators.Restaurant;
using ANGI.Application.Common.Exceptions;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using FluentValidation;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Restaurant.AddMenuSubmissionItem;

/// <summary>Verifies AddMenuSubmissionItemUseCase adds a valid dish to an owned draft menu with a safe sort order and cover.</summary>
public sealed class AddMenuSubmissionItemUseCaseTests
{
    // TEST-01: Add a normalized new dish at the end of an editable draft.
    /// <summary>Verifies the successful OWN-14 proposal workflow.</summary>
    [Fact]
    public async Task ExecuteAsync_WithValidProposal_ShouldAddNewItem()
    {
        // STEP-01: Create submission as MenuSubmission and add a MenuSubmissionItem to submission.Items.
        var submission = new MenuSubmission { Id = 14 };
        submission.Items.Add(new MenuSubmissionItem { Id = 1, SortOrder = 3 });
        // STEP-02: Mock IRestaurantMenuRepository, ICurrentUserService and 1 more; stub repository.LockOwnedSubmissionAsync, repository.GetExistingTagIdsAsync and 4 more.
        var repository = new Mock<IRestaurantMenuRepository>();
        repository.Setup(item => item.LockOwnedSubmissionAsync(12, 14, It.IsAny<CancellationToken>()))
            .ReturnsAsync(submission);
        repository.Setup(item => item.GetExistingTagIdsAsync(
                It.IsAny<IReadOnlyCollection<short>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([(short)2]);
        repository.Setup(item => item.AddItem(It.IsAny<MenuSubmissionItem>()))
            .Callback<MenuSubmissionItem>(item => item.Id = 340);
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(item => item.UserId).Returns(12);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(item => item.BeginTransactionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IUnitOfWorkTransaction>());
        unitOfWork.Setup(item => item.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
        // STEP-03: Create the AddMenuSubmissionItemUseCase under test as useCase.
        var useCase = new AddMenuSubmissionItemUseCase(
            new AddMenuSubmissionItemRequestDtoValidator(),
            repository.Object,
            Mock.Of<IMediaRepository>(),
            currentUser.Object,
            unitOfWork.Object);

        // STEP-04: Run useCase.ExecuteAsync with AddMenuSubmissionItemRequestDto { Name, Description, DishForm, ... } and keep result.
        var result = await useCase.ExecuteAsync(14, new AddMenuSubmissionItemRequestDto
        {
            Name = " Mì Quảng gà ",
            Description = "Sợi mì vàng, nước lèo sệt và thịt gà ta.",
            DishForm = "dry",
            Price = 40000,
            TagIds = [2]
        }, CancellationToken.None);

        // STEP-05: Assert result.Id is 340, result.Change is "new", result.Name is the expected text and result.SortOrder is 4.
        result.Id.Should().Be(340);
        result.Change.Should().Be("new");
        result.Name.Should().Be("Mì Quảng gà");
        result.SortOrder.Should().Be(4);
    }

    // TEST-02: Reject adding an item when the submission is no longer a draft.
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
        repository.Setup(item => item.LockOwnedSubmissionAsync(12, 14, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MenuSubmission { Id = 14, Status = status });
        // STEP-02: Build useCase under test through CreateUseCase.
        var useCase = CreateUseCase(repository, new Mock<IMediaRepository>());

        // STEP-03: Wrap useCase.ExecuteAsync in act so its outcome can be asserted.
        var act = () => useCase.ExecuteAsync(14, ValidRequest(), CancellationToken.None);

        // STEP-04: Assert act throws ConflictException and exception.ErrorCode is "SUBMISSION_NOT_EDITABLE".
        var exception = await act.Should().ThrowAsync<ConflictException>();
        exception.Which.ErrorCode.Should().Be("SUBMISSION_NOT_EDITABLE");
    }

    // TEST-03: Hide missing and unowned submissions behind the not-found response.
    /// <summary>Ensures another owner's submission is indistinguishable from a missing one.</summary>
    [Fact]
    public async Task ExecuteAsync_WithUnknownSubmission_ShouldThrowNotFound()
    {
        // STEP-01: Mock IRestaurantMenuRepository; stub repository.LockOwnedSubmissionAsync.
        var repository = new Mock<IRestaurantMenuRepository>();
        repository.Setup(item => item.LockOwnedSubmissionAsync(12, 14, It.IsAny<CancellationToken>()))
            .ReturnsAsync((MenuSubmission?)null);

        // STEP-02: Wrap CreateUseCase().ExecuteAsync in act so its outcome can be asserted.
        var act = () => CreateUseCase(repository, new Mock<IMediaRepository>())
            .ExecuteAsync(14, ValidRequest(), CancellationToken.None);

        // STEP-03: Assert act throws NotFoundException.
        await act.Should().ThrowAsync<NotFoundException>();
    }

    // TEST-04: Reject a tag identifier that does not exist in the catalog.
    /// <summary>Ensures a dish can only reference tags from the catalog.</summary>
    [Fact]
    public async Task ExecuteAsync_WithNonexistentTag_ShouldRejectBeforeAdding()
    {
        // STEP-01: Get repository from DraftRepository.
        var repository = DraftRepository();
        // STEP-02: Stub repository.GetExistingTagIdsAsync.
        repository.Setup(item => item.GetExistingTagIdsAsync(
                It.IsAny<IReadOnlyCollection<short>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        // STEP-03: Create request through ValidRequest and set request.TagIds to a test items.
        var request = ValidRequest();
        request.TagIds = [2];

        // STEP-04: Wrap CreateUseCase().ExecuteAsync with request in act so its outcome can be asserted.
        var act = () => CreateUseCase(repository, new Mock<IMediaRepository>())
            .ExecuteAsync(14, request, CancellationToken.None);

        // STEP-05: Assert act throws ValidationException.
        await act.Should().ThrowAsync<ValidationException>();
        // STEP-06: Verify AddItem is never called.
        repository.Verify(item => item.AddItem(It.IsAny<MenuSubmissionItem>()), Times.Never);
    }

    // TEST-05: Reject PDFs and images uploaded for a non-dish purpose.
    /// <summary>Ensures only a dish image can become a dish cover.</summary>
    [Theory]
    [InlineData("application/pdf", "raw/authenticated/verification_doc/file.pdf")]
    [InlineData("image/jpeg", "image/upload/verification_doc/file.jpg")]
    public async Task ExecuteAsync_WithPdfOrNonDishImage_ShouldRejectCover(
        string mimeType,
        string storageKey)
    {
        // STEP-01: Get repository from DraftRepository.
        var repository = DraftRepository();
        // STEP-02: Mock IMediaRepository; stub mediaRepository.GetOwnedByIdsAsync.
        var mediaRepository = new Mock<IMediaRepository>();
        mediaRepository.Setup(item => item.GetOwnedByIdsAsync(
                12, It.IsAny<IReadOnlyCollection<long>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MediaFile
            {
                Id = 50,
                MimeType = mimeType,
                StorageKey = storageKey
            }]);
        // STEP-03: Create request through ValidRequest and set request.CoverMediaId to 50.
        var request = ValidRequest();
        request.CoverMediaId = 50;

        // STEP-04: Wrap CreateUseCase().ExecuteAsync with request in act so its outcome can be asserted.
        var act = () => CreateUseCase(repository, mediaRepository)
            .ExecuteAsync(14, request, CancellationToken.None);

        // STEP-05: Assert act throws ValidationException.
        await act.Should().ThrowAsync<ValidationException>();
    }

    // TEST-06: Keep an existing cover media entity detached from the new EF graph.
    /// <summary>Prevents EF from inserting a duplicate of an existing media row.</summary>
    [Fact]
    public async Task ExecuteAsync_WithOwnedDishImage_ShouldKeepMediaDetachedFromAddedGraph()
    {
        // STEP-01: Get repository from DraftRepository.
        var repository = DraftRepository();
        // STEP-02: Declare added to capture a call argument; mock IMediaRepository; stub repository.AddItem and mediaRepository.GetOwnedByIdsAsync.
        MenuSubmissionItem? added = null;
        repository.Setup(item => item.AddItem(It.IsAny<MenuSubmissionItem>()))
            .Callback<MenuSubmissionItem>(value => added = value);
        var mediaRepository = new Mock<IMediaRepository>();
        mediaRepository.Setup(item => item.GetOwnedByIdsAsync(
                12, It.IsAny<IReadOnlyCollection<long>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MediaFile
            {
                Id = 50,
                MimeType = "image/jpeg",
                StorageKey = "image/upload/dish/file.jpg",
                Url = "https://cdn.test/file.jpg"
            }]);
        // STEP-03: Create request through ValidRequest and set request.CoverMediaId to 50.
        var request = ValidRequest();
        request.CoverMediaId = 50;

        // STEP-04: Run CreateUseCase().ExecuteAsync with request and keep result.
        var result = await CreateUseCase(repository, mediaRepository)
            .ExecuteAsync(14, request, CancellationToken.None);

        // STEP-05: Assert added.CoverMedia is null, added.CoverMediaId is 50 and result.CoverUrl is "https://cdn.test/file.jpg".
        added!.CoverMedia.Should().BeNull();
        added.CoverMediaId.Should().Be(50);
        result.CoverUrl.Should().Be("https://cdn.test/file.jpg");
    }

    // TEST-07: Assign sort order zero when the draft menu has no items.
    /// <summary>Ensures the first dish of an empty draft starts the sort order at zero.</summary>
    [Fact]
    public async Task ExecuteAsync_WithEmptyMenu_ShouldAutoAssignZeroSortOrder()
    {
        // STEP-01: Get repository from DraftRepository.
        var repository = DraftRepository();

        // STEP-02: Run CreateUseCase().ExecuteAsync and keep result.
        var result = await CreateUseCase(repository, new Mock<IMediaRepository>())
            .ExecuteAsync(14, ValidRequest(), CancellationToken.None);

        // STEP-03: Assert result.SortOrder is 0.
        result.SortOrder.Should().Be(0);
    }

    // TEST-08: Avoid smallint overflow when the current menu already uses the maximum sort order.
    /// <summary>Guards against smallint overflow when appending to a full sort order range.</summary>
    [Fact]
    public async Task ExecuteAsync_WithMaximumExistingSortOrder_ShouldKeepMaximumSortOrder()
    {
        // STEP-01: Create submission as MenuSubmission and add a MenuSubmissionItem to submission.Items.
        var submission = new MenuSubmission { Id = 14 };
        submission.Items.Add(new MenuSubmissionItem { Id = 1, SortOrder = short.MaxValue });
        // STEP-02: Mock IRestaurantMenuRepository; stub repository.LockOwnedSubmissionAsync.
        var repository = new Mock<IRestaurantMenuRepository>();
        repository.Setup(item => item.LockOwnedSubmissionAsync(12, 14, It.IsAny<CancellationToken>()))
            .ReturnsAsync(submission);

        // STEP-03: Run CreateUseCase().ExecuteAsync and keep result.
        var result = await CreateUseCase(repository, new Mock<IMediaRepository>())
            .ExecuteAsync(14, ValidRequest(), CancellationToken.None);

        // STEP-04: Assert result.SortOrder is short.MaxValue.
        result.SortOrder.Should().Be(short.MaxValue);
    }

    /// <summary>Creates a repository mock that returns draft submission 14 for owner 12.</summary>
    private static Mock<IRestaurantMenuRepository> DraftRepository()
    {
        var repository = new Mock<IRestaurantMenuRepository>();
        repository.Setup(item => item.LockOwnedSubmissionAsync(12, 14, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MenuSubmission { Id = 14 });
        return repository;
    }

    /// <summary>Creates a valid add-item request without tags or cover.</summary>
    private static AddMenuSubmissionItemRequestDto ValidRequest() => new()
    {
        Name = "Dish",
        Description = new string('a', 30),
        DishForm = "dry",
        Price = 40_000,
        TagIds = []
    };

    /// <summary>Creates the use case for owner 12 with the real validator and the given repositories.</summary>
    private static AddMenuSubmissionItemUseCase CreateUseCase(
        Mock<IRestaurantMenuRepository> repository,
        Mock<IMediaRepository> mediaRepository)
    {
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(item => item.UserId).Returns(12);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(item => item.BeginTransactionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IUnitOfWorkTransaction>());
        return new AddMenuSubmissionItemUseCase(
            new AddMenuSubmissionItemRequestDtoValidator(),
            repository.Object,
            mediaRepository.Object,
            currentUser.Object,
            unitOfWork.Object);
    }
}
