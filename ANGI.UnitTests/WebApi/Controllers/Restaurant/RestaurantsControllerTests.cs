using ANGI.Application.Common.Interfaces.UseCases.Restaurant;
using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Restaurant;
using ANGI.WebApi.Common.Models;
using ANGI.WebApi.Controllers.Restaurant;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace ANGI.UnitTests.WebApi.Controllers.Restaurant;

public sealed class RestaurantsControllerTests
{
    private readonly Mock<IBrowseRestaurantsUseCase> _browseUseCaseMock = new();
    private readonly Mock<IGetRestaurantDetailUseCase> _getDetailUseCaseMock = new();
    private readonly Mock<IGetRestaurantMenuUseCase> _getMenuUseCaseMock = new();
    private readonly Mock<IGetRestaurantReviewsUseCase> _getReviewsUseCaseMock = new();

    private RestaurantsController CreateController() =>
        new(
            _browseUseCaseMock.Object,
            _getDetailUseCaseMock.Object,
            _getMenuUseCaseMock.Object,
            _getReviewsUseCaseMock.Object);

    // TEST-01: Return REST-01 payload wrapped in ApiResponse with HTTP 200 OK
    [Fact]
    public async Task Browse_WithValidRequest_ShouldReturnOkApiResponse()
    {
        var request = new BrowseRestaurantsRequestDto
        {
            Q = "Phở",
            Page = 1,
            PageSize = 20
        };

        var expectedPaged = new Paged<RestaurantCardDto>
        {
            Items = new[]
            {
                new RestaurantCardDto
                {
                    Id = 10,
                    Name = "Phở Gia Truyền",
                    Slug = "pho-gia-truyen",
                    AddressLine = "49 Bát Đàn",
                    District = "Hoàn Kiếm",
                    ProvinceName = "Thành phố Hà Nội",
                    RatingAvg = 4.6m,
                    RatingCount = 200,
                    OperatingStatus = "open",
                    IsOpenNow = true
                }
            },
            Page = 1,
            PageSize = 20,
            TotalItems = 1,
            TotalPages = 1
        };

        _browseUseCaseMock
            .Setup(u => u.ExecuteAsync(request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedPaged);

        var controller = CreateController();
        var actionResult = await controller.Browse(request, CancellationToken.None);

        var objectResult = actionResult.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(StatusCodes.Status200OK);

        var response = objectResult.Value.Should().BeOfType<ApiResponse<Paged<RestaurantCardDto>>>().Subject;
        response.Success.Should().BeTrue();
        response.Data.Should().BeSameAs(expectedPaged);
    }

    // TEST-02: Return REST-02 payload wrapped in ApiResponse with HTTP 200 OK
    [Fact]
    public async Task GetDetail_WithValidId_ShouldReturnOkApiResponse()
    {
        var expectedDetail = new RestaurantDetailDto
        {
            Id = 7,
            Name = "Bún Bò Bà Diệu",
            Slug = "bun-bo-ba-dieu",
            AddressLine = "12 Lê Duẩn",
            RatingAvg = 4.6m,
            RatingCount = 213,
            OperatingStatus = "open",
            IsOpenNow = true
        };

        _getDetailUseCaseMock
            .Setup(u => u.ExecuteAsync(It.Is<GetRestaurantDetailRequestDto>(r => r.RestaurantId == 7), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedDetail);

        var controller = CreateController();
        var actionResult = await controller.GetDetail(7, lat: null, lng: null, include: null, CancellationToken.None);

        var objectResult = actionResult.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(StatusCodes.Status200OK);

        var response = objectResult.Value.Should().BeOfType<ApiResponse<RestaurantDetailDto>>().Subject;
        response.Success.Should().BeTrue();
        response.Data.Should().BeSameAs(expectedDetail);
    }

    // TEST-03: Return REST-03 payload wrapped in ApiResponse with HTTP 200 OK
    [Fact]
    public async Task GetMenu_WithValidId_ShouldReturnOkApiResponse()
    {
        var expectedMenu = new RestaurantMenuDto
        {
            RestaurantId = 7,
            Dishes = Array.Empty<ANGI.Application.DTOs.Discovery.DishCardDto>()
        };

        _getMenuUseCaseMock
            .Setup(u => u.ExecuteAsync(It.Is<GetRestaurantMenuRequestDto>(r => r.RestaurantId == 7), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedMenu);

        var controller = CreateController();
        var actionResult = await controller.GetMenu(7, tagIds: null, includeUnavailable: true, CancellationToken.None);

        var objectResult = actionResult.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(StatusCodes.Status200OK);

        var response = objectResult.Value.Should().BeOfType<ApiResponse<RestaurantMenuDto>>().Subject;
        response.Success.Should().BeTrue();
        response.Data.Should().BeSameAs(expectedMenu);
    }

    // TEST-04: Return REST-04 payload wrapped in ApiResponse with HTTP 200 OK
    [Fact]
    public async Task GetReviews_WithValidId_ShouldReturnOkApiResponse()
    {
        var expectedReviews = new RestaurantReviewsDto
        {
            Items = Array.Empty<ReviewDto>(),
            Page = 1,
            PageSize = 20,
            TotalItems = 0,
            TotalPages = 0,
            RatingAvg = 4.6m,
            RatingCount = 213
        };

        _getReviewsUseCaseMock
            .Setup(u => u.ExecuteAsync(It.Is<GetRestaurantReviewsRequestDto>(r => r.RestaurantId == 7), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedReviews);

        var controller = CreateController();
        var actionResult = await controller.GetReviews(7, rating: null, hasImage: null, sort: null, page: 1, pageSize: 20, CancellationToken.None);

        var objectResult = actionResult.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(StatusCodes.Status200OK);

        var response = objectResult.Value.Should().BeOfType<ApiResponse<RestaurantReviewsDto>>().Subject;
        response.Success.Should().BeTrue();
        response.Data.Should().BeSameAs(expectedReviews);
    }
}
