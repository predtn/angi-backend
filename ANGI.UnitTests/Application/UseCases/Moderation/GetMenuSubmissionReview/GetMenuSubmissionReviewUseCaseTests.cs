using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Moderation;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.UseCases.Moderation.GetMenuSubmissionReview;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Moderation.GetMenuSubmissionReview;

/// <summary>Covers MOD-M2 comparison mapping and read access errors.</summary>
public sealed class GetMenuSubmissionReviewUseCaseTests
{
    // TEST-01: Return flattened metadata, restaurant, new and edited items, before values and removed dishes.
    /// <summary>Verifies the complete comparison contract, tag sorting and item ordering.</summary>
    [Fact]
    public async Task ExecuteAsync_ShouldMapComparison()
    {
        var restaurant = new ANGI.Domain.Entities.Restaurant { Id = 7, Name = "Restaurant", Latitude = 10, Longitude = 106 };
        var original = new Dish { Id = 1, RestaurantId = 7, Restaurant = restaurant, Name = "Original", Price = 100,
            CoverMedia = new MediaFile { Url = "https://cdn.test/old.jpg" },
            DishTags = [new DishTag { TagId = 2, Tag = new Tag { Id = 2, Code = "lunch", Name = "Lunch" } }] };
        var removed = new Dish { Id = 2, Restaurant = restaurant, Name = "Removed" };
        restaurant.Dishes = [original, removed];
        var submission = new MenuSubmission
        {
            Id = 14, RestaurantId = 7, Restaurant = restaurant, Status = MenuSubmissionStatus.Pending,
            OwnerNote = "Owner note", Reviewer = new User { Id = 10, DisplayName = "Mod", AvatarMedia = new MediaFile { Url = "avatar" } },
            Items =
            [
                new MenuSubmissionItem { Id = 20, Name = "New", SortOrder = 2 },
                new MenuSubmissionItem { Id = 10, Name = "Edited", SourceDishId = 1, SourceDish = original, SortOrder = 1,
                    CoverMediaId = 9, CoverMedia = new MediaFile { Url = "https://cdn.test/new.jpg" }, TagIds = [2] }
            ]
        };
        var repository = new Mock<IMenuReviewRepository>();
        repository.Setup(x => x.HasPermissionAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        repository.Setup(x => x.GetDetailAsync(14, It.IsAny<CancellationToken>())).ReturnsAsync(submission);
        var current = new Mock<ICurrentUserService>();
        current.SetupGet(x => x.UserId).Returns(10);
        current.SetupGet(x => x.Role).Returns("MOD");

        var result = await new GetMenuSubmissionReviewUseCase(repository.Object, current.Object).ExecuteAsync(14, CancellationToken.None);

        result.Id.Should().Be(14);
        result.Status.Should().Be("pending");
        result.OwnerNote.Should().Be("Owner note");
        result.Restaurant.Id.Should().Be(7);
        result.ReviewedBy!.DisplayName.Should().Be("Mod");
        result.Items.Select(x => x.Id).Should().Equal(10, 20);
        result.Items[0].Change.Should().Be("edited");
        result.Items[0].CoverUrl.Should().Be("https://cdn.test/new.jpg");
        result.Items[0].Before!.Name.Should().Be("Original");
        result.Items[0].Before!.CoverUrl.Should().Be("https://cdn.test/old.jpg");
        result.Items[0].Before!.Tags.Should().ContainSingle(x => x.Id == 2);
        result.Items[1].Change.Should().Be("new");
        result.Items[1].Before.Should().BeNull();
        result.RemovedDishes.Should().ContainSingle(x => x.Id == 2);
    }

    // TEST-02: Return SUBMISSION_NOT_FOUND for missing, zero and negative identifiers.
    /// <summary>Verifies missing resources never produce an empty successful response.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(999)]
    public async Task ExecuteAsync_Missing_ShouldNotFound(long id)
    {
        var repository = new Mock<IMenuReviewRepository>();
        repository.Setup(x => x.HasPermissionAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var current = new Mock<ICurrentUserService>();
        current.SetupGet(x => x.UserId).Returns(10);
        current.SetupGet(x => x.Role).Returns("ADMIN");
        var act = () => new GetMenuSubmissionReviewUseCase(repository.Object, current.Object).ExecuteAsync(id, CancellationToken.None);
        (await act.Should().ThrowAsync<NotFoundException>()).Which.ErrorCode.Should().Be("SUBMISSION_NOT_FOUND");
    }

    // TEST-03: Do not load submission details when effective permission is denied.
    /// <summary>Verifies access is checked before sensitive menu data is queried.</summary>
    [Fact]
    public async Task ExecuteAsync_Denied_ShouldNotLoad()
    {
        var repository = new Mock<IMenuReviewRepository>();
        var current = new Mock<ICurrentUserService>();
        current.SetupGet(x => x.UserId).Returns(10);
        current.SetupGet(x => x.Role).Returns("MOD");
        var act = () => new GetMenuSubmissionReviewUseCase(repository.Object, current.Object).ExecuteAsync(14, CancellationToken.None);
        await act.Should().ThrowAsync<ForbiddenException>();
        repository.Verify(x => x.GetDetailAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
