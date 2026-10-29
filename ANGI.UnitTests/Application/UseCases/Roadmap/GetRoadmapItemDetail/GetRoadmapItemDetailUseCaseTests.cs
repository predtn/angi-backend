using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Restaurant;
using ANGI.Application.Common.Interfaces.Repositories.Roadmap;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.UseCases.Roadmap.GetRoadmapItemDetail;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Roadmap.GetRoadmapItemDetail;

public sealed class GetRoadmapItemDetailUseCaseTests
{
    private static readonly DateTimeOffset MondayTenAm = new(2026, 10, 12, 3, 0, 0, TimeSpan.Zero);

    // TEST-01: Return every RM-12 field for an owned item with feedback and no restaurant review.
    /// <summary>Verifies complete nested mapping, nullable rank and distance, opening hours, feedback, and rating eligibility.</summary>
    [Fact]
    public async Task ExecuteAsync_WithOwnedItemAndNoReview_ShouldReturnCompleteRateableDetail()
    {
        var item = CreateItem();
        var roadmapRepository = RoadmapRepositoryReturning(item, FeedbackValue.Like);
        var restaurantRepository = RestaurantRepositoryReturning(null);
        var useCase = CreateUseCase(roadmapRepository.Object, restaurantRepository.Object, userId: 12);
        using var cancellation = new CancellationTokenSource();

        var result = await useCase.ExecuteAsync(88, 1502, cancellation.Token);

        result.Should().BeEquivalentTo(new
        {
            Id = 1502L,
            DayId = 301L,
            DayNumber = (short)1,
            MealSlot = "breakfast",
            SortOrder = (short)0,
            Source = "manual",
            RecoRequestId = item.RecoRequestId,
            MyFeedback = "like",
            CreatedAt = item.CreatedAt,
            CanRate = true,
            MyReview = (object?)null
        }, options => options.ExcludingMissingMembers());
        result.Dish.Id.Should().Be(105);
        result.Dish.Status.Should().Be("hidden");
        result.Dish.Rank.Should().BeNull();
        result.Dish.Images.Should().Equal("https://cdn.test/dish-1.jpg", "https://cdn.test/dish-2.jpg");
        result.Dish.Tags.Select(tag => tag.Id).Should().Equal((short)2, (short)5);
        result.Dish.Restaurant.DistanceKm.Should().BeNull();
        result.Dish.Restaurant.IsOpenNow.Should().BeTrue();
        result.Restaurant.BusinessHours.Should().ContainSingle();
        result.Restaurant.MyReview.Should().BeNull();
        roadmapRepository.Verify(repository => repository.GetOwnedItemDetailAsync(
            88, 1502, 12, cancellation.Token), Times.Once);
        roadmapRepository.Verify(repository => repository.GetFeedbackByItemIdsAsync(
            12, It.Is<IReadOnlyCollection<long>>(ids => ids.SequenceEqual(new long[] { 1502 })), cancellation.Token),
            Times.Once);
        restaurantRepository.Verify(repository => repository.GetTravelerReviewForRestaurantAsync(
            7, 12, cancellation.Token), Times.Once);
    }

    // TEST-02: A prior moderated review still makes the item non-rateable and is returned as the caller's review.
    /// <summary>Verifies canRate is based on review existence rather than public visibility.</summary>
    [Fact]
    public async Task ExecuteAsync_WithHiddenOwnReview_ShouldReturnReviewAndDisableRating()
    {
        var item = CreateItem();
        var review = CreateReview(item.Dish.Restaurant, ContentStatus.Hidden);
        var roadmapRepository = RoadmapRepositoryReturning(item, feedback: null);
        var restaurantRepository = RestaurantRepositoryReturning(review);
        var useCase = CreateUseCase(roadmapRepository.Object, restaurantRepository.Object, userId: 12);

        var result = await useCase.ExecuteAsync(88, 1502, CancellationToken.None);

        result.CanRate.Should().BeFalse();
        result.MyReview.Should().NotBeNull();
        result.MyReview!.Status.Should().Be("hidden");
        result.MyReview.Images.Should().ContainSingle().Which.Should().Be("https://cdn.test/review.jpg");
        result.Restaurant.MyReview.Should().BeSameAs(result.MyReview);
        result.MyFeedback.Should().BeNull();
    }

    // TEST-03: Hide a missing item, foreign roadmap, mismatched roadmap, and deleted roadmap behind one error.
    /// <summary>Verifies RM-12 emits only ROADMAP_ITEM_NOT_FOUND and stops dependent lookups after ownership fails.</summary>
    [Fact]
    public async Task ExecuteAsync_WithoutOwnedMatchingItem_ShouldThrowRoadmapItemNotFound()
    {
        var roadmapRepository = RoadmapRepositoryReturning(null, feedback: null);
        var restaurantRepository = new Mock<IRestaurantRepository>();
        var useCase = CreateUseCase(roadmapRepository.Object, restaurantRepository.Object, userId: 12);

        var act = () => useCase.ExecuteAsync(88, 999, CancellationToken.None);

        (await act.Should().ThrowAsync<NotFoundException>()).Which.ErrorCode.Should().Be("ROADMAP_ITEM_NOT_FOUND");
        roadmapRepository.Verify(repository => repository.GetFeedbackByItemIdsAsync(
            It.IsAny<int>(), It.IsAny<IReadOnlyCollection<long>>(), It.IsAny<CancellationToken>()), Times.Never);
        restaurantRepository.VerifyNoOtherCalls();
    }

    // TEST-04: Reject execution without an authenticated user before any persistence call.
    /// <summary>Verifies the Application boundary does not rely solely on controller authorization.</summary>
    [Fact]
    public async Task ExecuteAsync_WithoutCurrentUser_ShouldThrowUnauthorized()
    {
        var roadmapRepository = new Mock<IRoadmapRepository>();
        var restaurantRepository = new Mock<IRestaurantRepository>();
        var useCase = CreateUseCase(roadmapRepository.Object, restaurantRepository.Object, userId: null);

        var act = () => useCase.ExecuteAsync(88, 1502, CancellationToken.None);

        (await act.Should().ThrowAsync<UnauthorizedException>()).Which.ErrorCode.Should().Be("UNAUTHORIZED");
        roadmapRepository.VerifyNoOtherCalls();
        restaurantRepository.VerifyNoOtherCalls();
    }

    /// <summary>Creates a fully navigable item graph covering every RM-12 nested data source.</summary>
    private static RoadmapItem CreateItem()
    {
        var restaurant = new Domain.Entities.Restaurant
        {
            Id = 7,
            Name = "Restaurant",
            Slug = "restaurant",
            AddressLine = "12 Street",
            ProvinceName = "Province",
            Latitude = 16.067812m,
            Longitude = 108.220116m,
            RatingAvg = 4.6m,
            RatingCount = 213,
            OperatingStatus = RestaurantOperatingStatus.Open,
            CoverMedia = new MediaFile { Url = "https://cdn.test/restaurant-cover.jpg" }
        };
        restaurant.BusinessHours.Add(new RestaurantBusinessHour
        {
            DayOfWeek = 1,
            OpenTime = new TimeOnly(6, 0),
            CloseTime = new TimeOnly(13, 30)
        });
        restaurant.Images.Add(new RestaurantImage
        {
            SortOrder = 0,
            Media = new MediaFile { Url = "https://cdn.test/restaurant.jpg" }
        });

        var dish = new Dish
        {
            Id = 105,
            RestaurantId = restaurant.Id,
            Restaurant = restaurant,
            Name = "Dish",
            Description = "Description",
            Price = 45_000,
            DishForm = DishForm.Soup,
            IsAvailable = false,
            Status = DishStatus.Hidden,
            LikeCount = 8,
            DislikeCount = 2,
            CoverMedia = new MediaFile { Url = "https://cdn.test/dish-cover.jpg" }
        };
        dish.Images.Add(new DishImage { SortOrder = 2, Media = new MediaFile { Url = "https://cdn.test/dish-2.jpg" } });
        dish.Images.Add(new DishImage { SortOrder = 1, Media = new MediaFile { Url = "https://cdn.test/dish-1.jpg" } });
        dish.DishTags.Add(new DishTag
        {
            TagId = 5,
            Tag = new Tag { Id = 5, TagType = TagType.Category, Code = "noodle", Name = "Noodle" }
        });
        dish.DishTags.Add(new DishTag
        {
            TagId = 2,
            Tag = new Tag { Id = 2, TagType = TagType.MealTime, Code = "breakfast", Name = "Breakfast" }
        });

        var roadmap = new Domain.Entities.Roadmap { Id = 88, UserId = 12, Title = "Trip", NumDays = 1 };
        var day = new RoadmapDay { Id = 301, RoadmapId = roadmap.Id, Roadmap = roadmap, DayNumber = 1 };
        return new RoadmapItem
        {
            Id = 1502,
            RoadmapDayId = day.Id,
            RoadmapDay = day,
            DishId = dish.Id,
            Dish = dish,
            MealSlot = MealSlot.Breakfast,
            SortOrder = 0,
            Source = RoadmapItemSource.Manual,
            RecoRequestId = Guid.Parse("6f0c2b1e-8d7a-4c1e-9b3f-2a5d7e9c1f00"),
            CreatedAt = new DateTime(2026, 10, 2, 2, 10, 0, DateTimeKind.Utc)
        };
    }

    /// <summary>Creates an own review with all navigations required by the shared review mapper.</summary>
    private static RestaurantReview CreateReview(Domain.Entities.Restaurant restaurant, ContentStatus status)
    {
        var review = new RestaurantReview
        {
            Id = 640,
            RestaurantId = restaurant.Id,
            Restaurant = restaurant,
            UserId = 12,
            User = new User { Id = 12, DisplayName = "Traveler" },
            Rating = 5,
            Content = "Great",
            RoadmapItemId = 1502,
            Status = status,
            CreatedAt = new DateTime(2026, 10, 3, 2, 0, 0, DateTimeKind.Utc)
        };
        review.Images.Add(new ReviewImage { Media = new MediaFile { Url = "https://cdn.test/review.jpg" } });
        return review;
    }

    /// <summary>Creates a roadmap repository mock with deterministic item and feedback results.</summary>
    private static Mock<IRoadmapRepository> RoadmapRepositoryReturning(RoadmapItem? item, FeedbackValue? feedback)
    {
        var repository = new Mock<IRoadmapRepository>();
        repository.Setup(value => value.GetOwnedItemDetailAsync(
                It.IsAny<long>(), It.IsAny<long>(), 12, It.IsAny<CancellationToken>()))
            .ReturnsAsync(item);
        repository.Setup(value => value.GetFeedbackByItemIdsAsync(
                12, It.IsAny<IReadOnlyCollection<long>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(feedback.HasValue
                ? new Dictionary<long, FeedbackValue> { [1502] = feedback.Value }
                : new Dictionary<long, FeedbackValue>());
        return repository;
    }

    /// <summary>Creates a restaurant repository mock with a deterministic own-review result.</summary>
    private static Mock<IRestaurantRepository> RestaurantRepositoryReturning(RestaurantReview? review)
    {
        var repository = new Mock<IRestaurantRepository>();
        repository.Setup(value => value.GetTravelerReviewForRestaurantAsync(7, 12, It.IsAny<CancellationToken>()))
            .ReturnsAsync(review);
        return repository;
    }

    /// <summary>Creates RM-12 with deterministic identity and Vietnam-local opening-time evaluation.</summary>
    private static GetRoadmapItemDetailUseCase CreateUseCase(
        IRoadmapRepository roadmapRepository,
        IRestaurantRepository restaurantRepository,
        int? userId)
    {
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(value => value.UserId).Returns(userId);
        return new GetRoadmapItemDetailUseCase(
            roadmapRepository,
            restaurantRepository,
            currentUser.Object,
            new FixedTimeProvider(MondayTenAm));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        /// <summary>Returns the fixed instant used by opening-hour test cases.</summary>
        public override DateTimeOffset GetUtcNow() => now;
    }
}
