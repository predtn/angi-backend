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

namespace ANGI.Test.Application.UseCases.Restaurant.AddMenuSubmissionItem;

public sealed class AddMenuSubmissionItemUseCaseTests
{
    // TEST-01: Add a normalized new dish at the end of an editable draft.
    /// <summary>Verifies the successful OWN-14 proposal workflow.</summary>
    [Fact]
    public async Task ExecuteAsync_WithValidProposal_ShouldAddNewItem()
    {
        var submission = new MenuSubmission { Id = 14 };
        submission.Items.Add(new MenuSubmissionItem { Id = 1, SortOrder = 3 });
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
        var useCase = new AddMenuSubmissionItemUseCase(
            new AddMenuSubmissionItemRequestDtoValidator(),
            repository.Object,
            Mock.Of<IMediaRepository>(),
            currentUser.Object,
            unitOfWork.Object);

        var result = await useCase.ExecuteAsync(14, new AddMenuSubmissionItemRequestDto
        {
            Name = " Mì Quảng gà ",
            Description = "Sợi mì vàng, nước lèo sệt và thịt gà ta.",
            DishForm = "dry",
            Price = 40000,
            TagIds = [2]
        }, CancellationToken.None);

        result.Id.Should().Be(340);
        result.Change.Should().Be("new");
        result.Name.Should().Be("Mì Quảng gà");
        result.SortOrder.Should().Be(4);
    }

    // TEST-02: Reject adding an item when the submission is no longer a draft.
    [Theory]
    [InlineData(MenuSubmissionStatus.Pending)]
    [InlineData(MenuSubmissionStatus.Approved)]
    [InlineData(MenuSubmissionStatus.Rejected)]
    [InlineData(MenuSubmissionStatus.Cancelled)]
    public async Task ExecuteAsync_WithNonDraftSubmission_ShouldThrowConflict(MenuSubmissionStatus status)
    {
        var repository = new Mock<IRestaurantMenuRepository>();
        repository.Setup(item => item.LockOwnedSubmissionAsync(12, 14, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MenuSubmission { Id = 14, Status = status });
        var useCase = CreateUseCase(repository, new Mock<IMediaRepository>());

        var act = () => useCase.ExecuteAsync(14, ValidRequest(), CancellationToken.None);

        var exception = await act.Should().ThrowAsync<ConflictException>();
        exception.Which.ErrorCode.Should().Be("SUBMISSION_NOT_EDITABLE");
    }

    // TEST-03: Hide missing and unowned submissions behind the not-found response.
    [Fact]
    public async Task ExecuteAsync_WithUnknownSubmission_ShouldThrowNotFound()
    {
        var repository = new Mock<IRestaurantMenuRepository>();
        repository.Setup(item => item.LockOwnedSubmissionAsync(12, 14, It.IsAny<CancellationToken>()))
            .ReturnsAsync((MenuSubmission?)null);

        var act = () => CreateUseCase(repository, new Mock<IMediaRepository>())
            .ExecuteAsync(14, ValidRequest(), CancellationToken.None);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    // TEST-04: Reject a tag identifier that does not exist in the catalog.
    [Fact]
    public async Task ExecuteAsync_WithNonexistentTag_ShouldRejectBeforeAdding()
    {
        var repository = DraftRepository();
        repository.Setup(item => item.GetExistingTagIdsAsync(
                It.IsAny<IReadOnlyCollection<short>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var request = ValidRequest();
        request.TagIds = [2];

        var act = () => CreateUseCase(repository, new Mock<IMediaRepository>())
            .ExecuteAsync(14, request, CancellationToken.None);

        await act.Should().ThrowAsync<ValidationException>();
        repository.Verify(item => item.AddItem(It.IsAny<MenuSubmissionItem>()), Times.Never);
    }

    // TEST-05: Reject PDFs and images uploaded for a non-dish purpose.
    [Theory]
    [InlineData("application/pdf", "raw/authenticated/verification_doc/file.pdf")]
    [InlineData("image/jpeg", "image/upload/verification_doc/file.jpg")]
    public async Task ExecuteAsync_WithPdfOrNonDishImage_ShouldRejectCover(
        string mimeType,
        string storageKey)
    {
        var repository = DraftRepository();
        var mediaRepository = new Mock<IMediaRepository>();
        mediaRepository.Setup(item => item.GetOwnedByIdsAsync(
                12, It.IsAny<IReadOnlyCollection<long>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MediaFile
            {
                Id = 50,
                MimeType = mimeType,
                StorageKey = storageKey
            }]);
        var request = ValidRequest();
        request.CoverMediaId = 50;

        var act = () => CreateUseCase(repository, mediaRepository)
            .ExecuteAsync(14, request, CancellationToken.None);

        await act.Should().ThrowAsync<ValidationException>();
    }

    // TEST-06: Keep an existing cover media entity detached from the new EF graph.
    [Fact]
    public async Task ExecuteAsync_WithOwnedDishImage_ShouldKeepMediaDetachedFromAddedGraph()
    {
        var repository = DraftRepository();
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
        var request = ValidRequest();
        request.CoverMediaId = 50;

        var result = await CreateUseCase(repository, mediaRepository)
            .ExecuteAsync(14, request, CancellationToken.None);

        added!.CoverMedia.Should().BeNull();
        added.CoverMediaId.Should().Be(50);
        result.CoverUrl.Should().Be("https://cdn.test/file.jpg");
    }

    // TEST-07: Assign sort order zero when the draft menu has no items.
    [Fact]
    public async Task ExecuteAsync_WithEmptyMenu_ShouldAutoAssignZeroSortOrder()
    {
        var repository = DraftRepository();

        var result = await CreateUseCase(repository, new Mock<IMediaRepository>())
            .ExecuteAsync(14, ValidRequest(), CancellationToken.None);

        result.SortOrder.Should().Be(0);
    }

    // TEST-08: Avoid smallint overflow when the current menu already uses the maximum sort order.
    [Fact]
    public async Task ExecuteAsync_WithMaximumExistingSortOrder_ShouldKeepMaximumSortOrder()
    {
        var submission = new MenuSubmission { Id = 14 };
        submission.Items.Add(new MenuSubmissionItem { Id = 1, SortOrder = short.MaxValue });
        var repository = new Mock<IRestaurantMenuRepository>();
        repository.Setup(item => item.LockOwnedSubmissionAsync(12, 14, It.IsAny<CancellationToken>()))
            .ReturnsAsync(submission);

        var result = await CreateUseCase(repository, new Mock<IMediaRepository>())
            .ExecuteAsync(14, ValidRequest(), CancellationToken.None);

        result.SortOrder.Should().Be(short.MaxValue);
    }

    private static Mock<IRestaurantMenuRepository> DraftRepository()
    {
        var repository = new Mock<IRestaurantMenuRepository>();
        repository.Setup(item => item.LockOwnedSubmissionAsync(12, 14, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MenuSubmission { Id = 14 });
        return repository;
    }

    private static AddMenuSubmissionItemRequestDto ValidRequest() => new()
    {
        Name = "Dish",
        Description = new string('a', 30),
        DishForm = "dry",
        Price = 40_000,
        TagIds = []
    };

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
