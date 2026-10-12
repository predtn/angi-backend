using ANGI.Application.Common.Interfaces.UseCases.Restaurant;
using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Restaurant;
using ANGI.WebApi.Common.Models;
using ANGI.WebApi.Controllers.Restaurant;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace ANGI.Test.WebApi.Controllers.Restaurant;

public sealed class RestaurantsControllerTests
{
    // TEST-01: Return REST-01 payload wrapped in ApiResponse with HTTP 200 OK
    /// <summary>Verifies that the restaurants controller delegates to the use case and wraps results in ApiResponse.</summary>
    [Fact]
    public async Task Browse_WithValidRequest_ShouldReturnOkApiResponse()
    {
        var request = new BrowseRestaurantsRequestDto
        {
            Q = "Phở",
            Page = 1,
            PageSize = 20
        };

        var expectedCards = new[]
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
        };

        var expectedPaged = new Paged<RestaurantCardDto>
        {
            Items = expectedCards,
            Page = 1,
            PageSize = 20,
            TotalItems = 1,
            TotalPages = 1
        };

        var useCaseMock = new Mock<IBrowseRestaurantsUseCase>();
        useCaseMock
            .Setup(u => u.ExecuteAsync(request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedPaged);

        var controller = new RestaurantsController(useCaseMock.Object);

        var actionResult = await controller.Browse(request, CancellationToken.None);

        var objectResult = actionResult.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(StatusCodes.Status200OK);

        var response = objectResult.Value.Should().BeOfType<ApiResponse<Paged<RestaurantCardDto>>>().Subject;
        response.Success.Should().BeTrue();
        response.Data.Should().BeSameAs(expectedPaged);
        useCaseMock.Verify(u => u.ExecuteAsync(request, It.IsAny<CancellationToken>()), Times.Once);
    }
}
