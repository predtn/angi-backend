using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Discovery;
using ANGI.Application.DTOs.Discovery;
using ANGI.Application.UseCases.Discovery.GetDishDetail;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Discovery.GetDishDetail;

public sealed class GetDishDetailUseCaseTests
{
    // Monday 2026-10-12 10:00 in Vietnam (03:00 UTC).
    private static readonly DateTimeOffset MondayTenAm = new(2026, 10, 12, 3, 0, 0, TimeSpan.Zero);

    private readonly Mock<IDishRepository> _dishRepository = new();

    private GetDishDetailUseCase CreateUseCase() => new(_dishRepository.Object, new FixedTimeProvider(MondayTenAm));

    private static Dish PublicDish()
    {
        var restaurant = new Domain.Entities.Restaurant
        {
            Id = 7,
            Name = "Bún Bò Bà Diệu",
            Slug = "bun-bo-ba-dieu",
            CoverMedia = new MediaFile { Id = 1, Url = "https://cdn.test/restaurants/7.jpg" },
            AddressLine = "12 Lê Duẩn",
            District = "Quận Hải Châu",
            ProvinceName = "Thành phố Đà Nẵng",
            Latitude = 16.067812m,
            Longitude = 108.220116m,
            PriceLevel = 1,
            RatingAvg = 4.6m,
            RatingCount = 213,
            OperatingStatus = RestaurantOperatingStatus.Open,
            BusinessHours =
            {
                new RestaurantBusinessHour { DayOfWeek = 1, OpenTime = new TimeOnly(6, 0), CloseTime = new TimeOnly(13, 30) }
            }
        };
        var dish = new Dish
        {
            Id = 105,
            RestaurantId = 7,
            Restaurant = restaurant,
            Name = "Bún bò Huế",
            Description = "Nước dùng hầm xương 8 tiếng, sả và mắm ruốc",
            Price = 45000,
            DishForm = DishForm.Soup,
            IsAvailable = true,
            LikeCount = 128,
            DislikeCount = 9,
            Status = DishStatus.Active,
            CoverMedia = new MediaFile { Id = 2, Url = "https://cdn.test/dishes/105.jpg" }
        };
        dish.DishTags.Add(new DishTag
        {
            TagId = 5,
            Tag = new Tag { Id = 5, TagType = TagType.Category, Code = "noodle", Name = "Món nước" }
        });
        dish.DishTags.Add(new DishTag
        {
            TagId = 2,
            Tag = new Tag { Id = 2, TagType = TagType.MealTime, Code = "lunch", Name = "Bữa trưa" }
        });
        dish.Images.Add(new DishImage { SortOrder = 2, Media = new MediaFile { Url = "https://cdn.test/dishes/105-2.jpg" } });
        dish.Images.Add(new DishImage { SortOrder = 1, Media = new MediaFile { Url = "https://cdn.test/dishes/105-1.jpg" } });
        return dish;
    }

    // TEST-01: Map every DishDetail field of the API Design, with the full RestaurantCard and ordered images and tags.
    [Fact]
    public async Task ExecuteAsync_WithPublicDish_ShouldReturnDocumentedDishDetail()
    {
        _dishRepository.Setup(x => x.GetPublicDetailAsync(105, It.IsAny<CancellationToken>())).ReturnsAsync(PublicDish());

        var result = await CreateUseCase().ExecuteAsync(105, CancellationToken.None);

        result.Should().BeEquivalentTo(new
        {
            Id = 105,
            Name = "Bún bò Huế",
            Price = 45000m,
            CoverUrl = "https://cdn.test/dishes/105.jpg",
            DishForm = "soup",
            IsAvailable = true,
            LikeCount = 128,
            DislikeCount = 9,
            Rank = (int?)null,
            Description = "Nước dùng hầm xương 8 tiếng, sả và mắm ruốc",
            Images = new[] { "https://cdn.test/dishes/105-1.jpg", "https://cdn.test/dishes/105-2.jpg" },
            Status = "active",
            Restaurant = new
            {
                Id = 7L,
                Name = "Bún Bò Bà Diệu",
                Slug = "bun-bo-ba-dieu",
                CoverUrl = "https://cdn.test/restaurants/7.jpg",
                AddressLine = "12 Lê Duẩn",
                District = "Quận Hải Châu",
                ProvinceName = "Thành phố Đà Nẵng",
                Latitude = 16.067812m,
                Longitude = 108.220116m,
                PriceLevel = (short?)1,
                RatingAvg = 4.6m,
                RatingCount = 213,
                OperatingStatus = "open",
                IsOpenNow = true,
                DistanceKm = (decimal?)null
            }
        }, options => options.WithStrictOrdering());
        result.Tags.Select(tag => (tag.Id, tag.TagType, tag.Code)).Should().Equal(
            ((short)2, "meal_time", "lunch"),
            ((short)5, "category", "noodle"));
    }

    // TEST-02: Report a closed restaurant and a dish without media as the API Design allows (null cover, no images).
    [Fact]
    public async Task ExecuteAsync_WithClosedRestaurantAndNoMedia_ShouldReturnFalseOpenAndEmptyImages()
    {
        var dish = PublicDish();
        dish.CoverMedia = null;
        dish.Images.Clear();
        dish.Restaurant.CoverMedia = null;
        dish.Restaurant.BusinessHours.Clear();
        _dishRepository.Setup(x => x.GetPublicDetailAsync(105, It.IsAny<CancellationToken>())).ReturnsAsync(dish);

        var result = await CreateUseCase().ExecuteAsync(105, CancellationToken.None);

        result.CoverUrl.Should().BeNull();
        result.Images.Should().BeEmpty();
        result.Restaurant.CoverUrl.Should().BeNull();
        result.Restaurant.IsOpenNow.Should().BeFalse();
    }

    // TEST-03: A dish the repository does not return (missing, hidden, removed or of a non-public restaurant) is 404.
    [Fact]
    public async Task ExecuteAsync_WithoutPublicDish_ShouldThrowDishNotFound()
    {
        _dishRepository.Setup(x => x.GetPublicDetailAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Dish?)null);

        var act = () => CreateUseCase().ExecuteAsync(999, CancellationToken.None);

        (await act.Should().ThrowAsync<NotFoundException>()).Which.ErrorCode.Should().Be("DISH_NOT_FOUND");
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
