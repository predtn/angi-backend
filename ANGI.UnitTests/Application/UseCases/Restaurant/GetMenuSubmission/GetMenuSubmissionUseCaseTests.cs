using ANGI.Application.Common.Interfaces.Repositories.Restaurant;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Exceptions;
using ANGI.Application.UseCases.Restaurant.GetMenuSubmission;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Restaurant.GetMenuSubmission;

public sealed class GetMenuSubmissionUseCaseTests
{
    // TEST-01: Mark changed source items as edited and current omitted dishes as removed.
    /// <summary>Verifies the OWN-13 comparison against the currently active menu.</summary>
    [Fact]
    public async Task ExecuteAsync_WithChangedAndOmittedDishes_ShouldMapDiff()
    {
        var restaurant = new Domain.Entities.Restaurant { Id = 7, Name = "Quán", Latitude = 1, Longitude = 2 };
        var retainedDish = new Dish
        {
            Id = 105,
            Restaurant = restaurant,
            Name = "Món cũ",
            Description = "Mô tả cũ",
            DishForm = DishForm.Soup,
            Price = 40000
        };
        var removedDish = new Dish { Id = 106, Restaurant = restaurant, Name = "Món bỏ", Price = 30000 };
        restaurant.Dishes.Add(retainedDish);
        restaurant.Dishes.Add(removedDish);
        var submission = new MenuSubmission { Id = 14, RestaurantId = 7, Restaurant = restaurant };
        submission.Items.Add(new MenuSubmissionItem
        {
            Id = 340,
            SourceDishId = 105,
            SourceDish = retainedDish,
            Name = "Món mới",
            Description = "Mô tả cũ",
            DishForm = DishForm.Soup,
            Price = 40000
        });
        var repository = new Mock<IRestaurantMenuRepository>();
        repository.Setup(item => item.GetSubmissionDetailsAsync(12, 14, It.IsAny<CancellationToken>()))
            .ReturnsAsync(submission);
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(item => item.UserId).Returns(12);
        var useCase = new GetMenuSubmissionUseCase(repository.Object, currentUser.Object);

        var result = await useCase.ExecuteAsync(14, CancellationToken.None);

        result.Items.Should().ContainSingle().Which.Change.Should().Be("edited");
        result.RemovedDishes.Should().ContainSingle().Which.Id.Should().Be(106);
    }

    // TEST-02: Hide missing and unowned submissions behind the not-found response.
    [Fact]
    public async Task ExecuteAsync_WithUnknownOrOtherOwnerSubmission_ShouldThrowNotFound()
    {
        var repository = new Mock<IRestaurantMenuRepository>();
        repository.Setup(item => item.GetSubmissionDetailsAsync(12, 14, It.IsAny<CancellationToken>()))
            .ReturnsAsync((MenuSubmission?)null);

        var act = () => CreateUseCase(repository).ExecuteAsync(14, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<NotFoundException>();
        exception.Which.ErrorCode.Should().Be("SUBMISSION_NOT_FOUND");
    }

    // TEST-03: Classify new, unchanged, and each independently edited field.
    [Fact]
    public async Task ExecuteAsync_ShouldMapNewUnchangedAndEveryEditableDifference()
    {
        var restaurant = new Domain.Entities.Restaurant { Id = 7 };
        var source = new Dish
        {
            Id = 100,
            Restaurant = restaurant,
            Name = "Original",
            Description = "Original description",
            DishForm = DishForm.Soup,
            Price = 10,
            CoverMediaId = 5,
            SortOrder = 1,
            DishTags = [new DishTag { DishId = 100, TagId = 2 }]
        };
        restaurant.Dishes.Add(source);
        var submission = new MenuSubmission { Id = 14, Restaurant = restaurant };
        submission.Items.Add(Clone(source, 1));
        submission.Items.Add(Clone(source, 2, item => item.Name = "Changed"));
        submission.Items.Add(Clone(source, 3, item => item.Description = "Changed"));
        submission.Items.Add(Clone(source, 4, item => item.Price = 11));
        submission.Items.Add(Clone(source, 5, item => item.DishForm = DishForm.Dry));
        submission.Items.Add(Clone(source, 6, item => item.CoverMediaId = 6));
        submission.Items.Add(Clone(source, 7, item => item.TagIds = [(short)3]));
        submission.Items.Add(Clone(source, 8, item => item.SortOrder = 2));
        submission.Items.Add(new MenuSubmissionItem { Id = 9, Name = "New", SortOrder = 9 });
        var repository = new Mock<IRestaurantMenuRepository>();
        repository.Setup(item => item.GetSubmissionDetailsAsync(12, 14, It.IsAny<CancellationToken>()))
            .ReturnsAsync(submission);

        var result = await CreateUseCase(repository).ExecuteAsync(14, CancellationToken.None);

        result.Items.Single(item => item.Id == 1).Change.Should().Be("unchanged");
        result.Items.Where(item => item.Id is >= 2 and <= 8)
            .Should().OnlyContain(item => item.Change == "edited");
        result.Items.Single(item => item.Id == 9).Change.Should().Be("new");
    }

    // TEST-04: Map reviewer details and deterministically order items and removed dishes.
    [Fact]
    public async Task ExecuteAsync_ShouldMapReviewerAvatarAndOrderItemsAndRemovedDishes()
    {
        var restaurant = new Domain.Entities.Restaurant { Id = 7 };
        restaurant.Dishes.Add(new Dish { Id = 12, Restaurant = restaurant, SortOrder = 2, Status = DishStatus.Hidden });
        restaurant.Dishes.Add(new Dish { Id = 11, Restaurant = restaurant, SortOrder = 2 });
        var submission = new MenuSubmission
        {
            Id = 14,
            Restaurant = restaurant,
            Reviewer = new User
            {
                Id = 8,
                DisplayName = "Reviewer",
                AvatarMedia = new MediaFile { Id = 9, Url = "https://cdn.test/avatar.jpg" }
            }
        };
        submission.Items.Add(new MenuSubmissionItem { Id = 3, Name = "B", SortOrder = 2 });
        submission.Items.Add(new MenuSubmissionItem { Id = 2, Name = "A", SortOrder = 2 });
        var repository = new Mock<IRestaurantMenuRepository>();
        repository.Setup(item => item.GetSubmissionDetailsAsync(12, 14, It.IsAny<CancellationToken>()))
            .ReturnsAsync(submission);

        var result = await CreateUseCase(repository).ExecuteAsync(14, CancellationToken.None);

        result.ReviewedBy.Should().BeEquivalentTo(new
        {
            Id = 8,
            DisplayName = "Reviewer",
            AvatarUrl = "https://cdn.test/avatar.jpg"
        });
        result.Items.Select(item => item.Id).Should().Equal(2, 3);
        result.RemovedDishes.Select(item => item.Id).Should().Equal(11, 12);
    }

    private static MenuSubmissionItem Clone(
        Dish source,
        long id,
        Action<MenuSubmissionItem>? mutate = null)
    {
        var item = new MenuSubmissionItem
        {
            Id = id,
            SourceDishId = source.Id,
            SourceDish = source,
            Name = source.Name,
            Description = source.Description,
            DishForm = source.DishForm,
            Price = source.Price,
            CoverMediaId = source.CoverMediaId,
            TagIds = source.DishTags.Select(value => value.TagId).ToArray(),
            SortOrder = source.SortOrder
        };
        mutate?.Invoke(item);
        return item;
    }

    private static GetMenuSubmissionUseCase CreateUseCase(Mock<IRestaurantMenuRepository> repository)
    {
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(item => item.UserId).Returns(12);
        return new GetMenuSubmissionUseCase(repository.Object, currentUser.Object);
    }
}
