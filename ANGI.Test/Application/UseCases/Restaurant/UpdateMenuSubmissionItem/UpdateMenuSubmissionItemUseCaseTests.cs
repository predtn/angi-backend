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

namespace ANGI.Test.Application.UseCases.Restaurant.UpdateMenuSubmissionItem;

public sealed class UpdateMenuSubmissionItemUseCaseTests
{
    // TEST-01: Apply supplied fields and distinguish an explicit null cover from an omitted cover.
    /// <summary>Verifies OWN-15 partial-update semantics including cover removal.</summary>
    [Fact]
    public async Task ExecuteAsync_WithExplicitNullCover_ShouldUpdateFieldsAndRemoveCover()
    {
        var item = new MenuSubmissionItem
        {
            Id = 340,
            SubmissionId = 14,
            Submission = new MenuSubmission { Id = 14 },
            Name = "Tên cũ",
            Description = "Mô tả cũ đủ dài cho món ăn hiện tại.",
            CoverMediaId = 9100
        };
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
        var useCase = new UpdateMenuSubmissionItemUseCase(
            new UpdateMenuSubmissionItemRequestDtoValidator(),
            repository.Object,
            Mock.Of<IMediaRepository>(),
            currentUser.Object,
            unitOfWork.Object);
        var request = new UpdateMenuSubmissionItemRequestDto
        {
            Name = " Tên mới ",
            CoverMediaId = null
        };

        var result = await useCase.ExecuteAsync(14, 340, request, CancellationToken.None);

        item.Name.Should().Be("Tên mới");
        item.CoverMediaId.Should().BeNull();
        result.CoverUrl.Should().BeNull();
        unitOfWork.Verify(value => value.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-02: Reject updates for every non-draft submission status.
    [Theory]
    [InlineData(MenuSubmissionStatus.Pending)]
    [InlineData(MenuSubmissionStatus.Approved)]
    [InlineData(MenuSubmissionStatus.Rejected)]
    [InlineData(MenuSubmissionStatus.Cancelled)]
    public async Task ExecuteAsync_WithNonDraftSubmission_ShouldThrowConflict(MenuSubmissionStatus status)
    {
        var repository = new Mock<IRestaurantMenuRepository>();
        repository.Setup(value => value.LockOwnedSubmissionAsync(12, 14, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MenuSubmission { Id = 14, Status = status });

        var act = () => CreateUseCase(repository, new Mock<IMediaRepository>()).ExecuteAsync(
            14, 340, new UpdateMenuSubmissionItemRequestDto(), CancellationToken.None);

        await act.Should().ThrowAsync<ConflictException>();
    }

    // TEST-03: Hide missing and unowned submissions or items behind the not-found response.
    [Fact]
    public async Task ExecuteAsync_WithUnknownSubmissionOrItem_ShouldThrowNotFound()
    {
        var repository = DraftRepository(item: null);

        var act = () => CreateUseCase(repository, new Mock<IMediaRepository>()).ExecuteAsync(
            14, 999, new UpdateMenuSubmissionItemRequestDto(), CancellationToken.None);

        var exception = await act.Should().ThrowAsync<NotFoundException>();
        exception.Which.ErrorCode.Should().Be("SUBMISSION_ITEM_NOT_FOUND");
    }

    // TEST-04: Preserve every field when the PATCH body is empty.
    [Fact]
    public async Task ExecuteAsync_WithEmptyPatch_ShouldKeepAllFieldsUnchanged()
    {
        var item = ExistingItem();
        var before = (item.Name, item.Description, item.DishForm, item.Price, item.CoverMediaId, item.SortOrder);
        var repository = DraftRepository(item);

        await CreateUseCase(repository, new Mock<IMediaRepository>()).ExecuteAsync(
            14, 340, new UpdateMenuSubmissionItemRequestDto(), CancellationToken.None);

        (item.Name, item.Description, item.DishForm, item.Price, item.CoverMediaId, item.SortOrder)
            .Should().Be(before);
    }

    // TEST-05: Apply every supplied patch field and preserve the omitted cover field.
    [Fact]
    public async Task ExecuteAsync_WithEveryPatchField_ShouldReplaceOnlySuppliedValues()
    {
        var item = ExistingItem();
        var repository = DraftRepository(item);
        repository.Setup(value => value.GetExistingTagIdsAsync(
                It.IsAny<IReadOnlyCollection<short>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([(short)3]);

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

        item.Name.Should().Be("New name");
        item.Description.Should().Be(new string('b', 30));
        item.DishForm.Should().Be(DishForm.Soup);
        item.Price.Should().Be(99);
        item.TagIds.Should().Equal((short)3);
        item.SortOrder.Should().Be(7);
        item.CoverMediaId.Should().Be(50);
    }

    // TEST-06: Reject PDF and non-dish-purpose media as a replacement cover.
    [Theory]
    [InlineData("application/pdf", "raw/authenticated/verification_doc/file.pdf")]
    [InlineData("image/jpeg", "image/upload/verification_doc/file.jpg")]
    public async Task ExecuteAsync_WithInvalidCoverPurposeOrMime_ShouldReject(
        string mime,
        string storageKey)
    {
        var repository = DraftRepository(ExistingItem());
        var mediaRepository = new Mock<IMediaRepository>();
        mediaRepository.Setup(value => value.GetOwnedByIdsAsync(
                12, It.IsAny<IReadOnlyCollection<long>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MediaFile { Id = 60, MimeType = mime, StorageKey = storageKey }]);

        var act = () => CreateUseCase(repository, mediaRepository).ExecuteAsync(
            14, 340, new UpdateMenuSubmissionItemRequestDto { CoverMediaId = 60 }, CancellationToken.None);

        await act.Should().ThrowAsync<ValidationException>();
    }

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
