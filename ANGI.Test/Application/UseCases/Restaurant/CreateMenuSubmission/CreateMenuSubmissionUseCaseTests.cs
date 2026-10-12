using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Restaurant;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.UseCases.Restaurant.CreateMenuSubmission;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using Moq;

namespace ANGI.Test.Application.UseCases.Restaurant.CreateMenuSubmission;

public sealed class CreateMenuSubmissionUseCaseTests
{
    // TEST-01: Clone every non-removed current dish into a new editable menu draft.
    /// <summary>Verifies the successful OWN-11 draft creation and transaction boundary.</summary>
    [Fact]
    public async Task ExecuteAsync_WithVerifiedRestaurant_ShouldCloneCurrentMenu()
    {
        var repository = new Mock<IRestaurantMenuRepository>();
        repository.Setup(item => item.LockOwnerRestaurantAsync(12, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Domain.Entities.Restaurant
            {
                Id = 7,
                OwnerId = 12,
                VerificationStatus = RestaurantVerificationStatus.Verified
            });
        repository.Setup(item => item.GetOpenSubmissionAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync((MenuSubmission?)null);
        repository.Setup(item => item.GetDraftSourceDishesAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new Dish
                {
                    Id = 105,
                    Name = "Bún bò Huế",
                    Description = "Nước dùng hầm xương và mắm ruốc đậm vị.",
                    DishForm = DishForm.Soup,
                    Price = 45000,
                    SortOrder = 1,
                    DishTags = [new DishTag { DishId = 105, TagId = 2 }]
                }
            ]);
        MenuSubmission? added = null;
        repository.Setup(item => item.AddSubmission(It.IsAny<MenuSubmission>()))
            .Callback<MenuSubmission>(submission => added = submission);
        var unitOfWork = new Mock<IUnitOfWork>();
        var transaction = new Mock<IUnitOfWorkTransaction>();
        unitOfWork.Setup(item => item.BeginTransactionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(transaction.Object);
        unitOfWork.Setup(item => item.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                added!.Id = 14;
                added.CreatedAt = new DateTime(2026, 10, 10, 2, 0, 0, DateTimeKind.Utc);
                added.Items.Single().Id = 340;
            })
            .ReturnsAsync(2);
        var useCase = CreateUseCase(repository, unitOfWork);

        var result = await useCase.ExecuteAsync(CancellationToken.None);

        result.Id.Should().Be(14);
        result.Status.Should().Be("draft");
        result.Items.Should().ContainSingle();
        result.Items[0].SourceDishId.Should().Be(105);
        result.Items[0].Change.Should().Be("unchanged");
        result.Items[0].TagIds.Should().Equal((short)2);
        transaction.Verify(item => item.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-02: Return the existing submission id when another draft or pending submission is open.
    /// <summary>Verifies the documented SUBMISSION_ALREADY_OPEN conflict payload source.</summary>
    [Fact]
    public async Task ExecuteAsync_WithOpenSubmission_ShouldThrowConflictWithId()
    {
        var repository = new Mock<IRestaurantMenuRepository>();
        repository.Setup(item => item.LockOwnerRestaurantAsync(12, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Domain.Entities.Restaurant
            {
                Id = 7,
                VerificationStatus = RestaurantVerificationStatus.Verified
            });
        repository.Setup(item => item.GetOpenSubmissionAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MenuSubmission { Id = 14, RestaurantId = 7 });
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(item => item.BeginTransactionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IUnitOfWorkTransaction>());
        var useCase = CreateUseCase(repository, unitOfWork);

        var act = () => useCase.ExecuteAsync(CancellationToken.None);

        var exception = await act.Should().ThrowAsync<SubmissionAlreadyOpenException>();
        exception.Which.SubmissionId.Should().Be(14);
        repository.Verify(item => item.AddSubmission(It.IsAny<MenuSubmission>()), Times.Never);
        unitOfWork.Verify(item => item.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-03: Reject draft creation until the owner's restaurant is verified.
    /// <summary>Verifies the documented RESTAURANT_NOT_VERIFIED error.</summary>
    [Fact]
    public async Task ExecuteAsync_WithUnverifiedRestaurant_ShouldThrowForbidden()
    {
        var repository = new Mock<IRestaurantMenuRepository>();
        repository.Setup(item => item.LockOwnerRestaurantAsync(12, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Domain.Entities.Restaurant
            {
                Id = 7,
                VerificationStatus = RestaurantVerificationStatus.Unverified
            });
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(item => item.BeginTransactionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IUnitOfWorkTransaction>());
        var useCase = CreateUseCase(repository, unitOfWork);

        var act = () => useCase.ExecuteAsync(CancellationToken.None);

        var exception = await act.Should().ThrowAsync<ForbiddenException>();
        exception.Which.ErrorCode.Should().Be("RESTAURANT_NOT_VERIFIED");
    }

    // TEST-04: Return RESTAURANT_NOT_FOUND when the authenticated owner has no restaurant.
    [Fact]
    public async Task ExecuteAsync_WithoutRestaurant_ShouldThrowNotFound()
    {
        var repository = new Mock<IRestaurantMenuRepository>();
        repository.Setup(item => item.LockOwnerRestaurantAsync(12, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Domain.Entities.Restaurant?)null);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(item => item.BeginTransactionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IUnitOfWorkTransaction>());

        var act = () => CreateUseCase(repository, unitOfWork).ExecuteAsync(CancellationToken.None);

        var exception = await act.Should().ThrowAsync<NotFoundException>();
        exception.Which.ErrorCode.Should().Be("RESTAURANT_NOT_FOUND");
    }

    // TEST-05: Create a valid draft with no items when the active menu is empty.
    [Fact]
    public async Task ExecuteAsync_WithEmptyMenu_ShouldCreateEmptyDraft()
    {
        var repository = VerifiedRestaurantRepository();
        repository.Setup(item => item.GetDraftSourceDishesAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        repository.Setup(item => item.AddSubmission(It.IsAny<MenuSubmission>()))
            .Callback<MenuSubmission>(submission => submission.Id = 14);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(item => item.BeginTransactionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IUnitOfWorkTransaction>());

        var result = await CreateUseCase(repository, unitOfWork).ExecuteAsync(CancellationToken.None);

        result.Id.Should().Be(14);
        result.Items.Should().BeEmpty();
    }

    // TEST-06: Clone media and tags by value without attaching existing entities to the new graph.
    [Fact]
    public async Task ExecuteAsync_WhenCloningMediaAndTags_ShouldKeepOnlyForeignKeysInNewGraph()
    {
        var media = new MediaFile { Id = 50, Url = "https://cdn.test/dish.jpg" };
        var dish = new Dish
        {
            Id = 105,
            Name = "Dish",
            CoverMediaId = 50,
            CoverMedia = media,
            DishTags = [new DishTag { DishId = 105, TagId = 2 }]
        };
        var repository = VerifiedRestaurantRepository();
        repository.Setup(item => item.GetDraftSourceDishesAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync([dish]);
        MenuSubmission? added = null;
        repository.Setup(item => item.AddSubmission(It.IsAny<MenuSubmission>()))
            .Callback<MenuSubmission>(submission => added = submission);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(item => item.BeginTransactionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IUnitOfWorkTransaction>());

        var result = await CreateUseCase(repository, unitOfWork).ExecuteAsync(CancellationToken.None);

        added!.Items.Single().SourceDish.Should().BeNull();
        added.Items.Single().CoverMedia.Should().BeNull();
        added.Items.Single().CoverMediaId.Should().Be(50);
        result.Items.Single().CoverUrl.Should().Be(media.Url);
    }

    // TEST-07: Leave the transaction uncommitted when persistence fails.
    [Fact]
    public async Task ExecuteAsync_WhenSaveFails_ShouldNotCommitTransaction()
    {
        var repository = VerifiedRestaurantRepository();
        repository.Setup(item => item.GetDraftSourceDishesAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var transaction = new Mock<IUnitOfWorkTransaction>();
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(item => item.BeginTransactionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(transaction.Object);
        unitOfWork.Setup(item => item.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("save failed"));

        var act = () => CreateUseCase(repository, unitOfWork).ExecuteAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        transaction.Verify(item => item.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-08: Reject an unauthenticated request before opening a transaction.
    [Fact]
    public async Task ExecuteAsync_WithoutAuthenticatedUser_ShouldThrowUnauthorizedBeforeTransaction()
    {
        var repository = new Mock<IRestaurantMenuRepository>();
        var currentUser = new Mock<ICurrentUserService>();
        var unitOfWork = new Mock<IUnitOfWork>();
        var useCase = new CreateMenuSubmissionUseCase(
            repository.Object,
            currentUser.Object,
            unitOfWork.Object);

        var act = () => useCase.ExecuteAsync(CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedException>();
        unitOfWork.Verify(item => item.BeginTransactionAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    private static Mock<IRestaurantMenuRepository> VerifiedRestaurantRepository()
    {
        var repository = new Mock<IRestaurantMenuRepository>();
        repository.Setup(item => item.LockOwnerRestaurantAsync(12, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Domain.Entities.Restaurant
            {
                Id = 7,
                OwnerId = 12,
                VerificationStatus = RestaurantVerificationStatus.Verified
            });
        repository.Setup(item => item.GetOpenSubmissionAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync((MenuSubmission?)null);
        return repository;
    }

    /// <summary>Creates the use case with a fixed authenticated owner.</summary>
    private static CreateMenuSubmissionUseCase CreateUseCase(
        Mock<IRestaurantMenuRepository> repository,
        Mock<IUnitOfWork> unitOfWork)
    {
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(item => item.UserId).Returns(12);
        return new CreateMenuSubmissionUseCase(repository.Object, currentUser.Object, unitOfWork.Object);
    }
}
