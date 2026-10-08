using ANGI.Application.Common.Interfaces.UseCases.Restaurant;
using ANGI.Application.DTOs.Restaurant;
using ANGI.WebApi.Common.Models;
using ANGI.WebApi.Controllers.Restaurant;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace ANGI.Test.WebApi.Controllers.Restaurant;

public sealed class OwnerRestaurantControllerTests
{
    // TEST-01: Return the OWN-01 payload in ApiResponse with HTTP 201.
    /// <summary>Verifies that the owner restaurant controller delegates and wraps the result.</summary>
    [Fact]
    public async Task Register_WithValidRequest_ShouldReturnCreatedApiResponse()
    {
        var request = new RegisterRestaurantRequestDto
        {
            Name = "Bún Bò Bà Diệu",
            Phone = "02363812345",
            AddressLine = "12 Lê Duẩn",
            District = "Hải Châu",
            ProvinceName = "Thành phố Đà Nẵng",
            Latitude = 16.067812m,
            Longitude = 108.220116m
        };
        var expected = new OwnerRestaurantDto { Id = 7, Name = request.Name };
        var useCase = new Mock<IRegisterRestaurantUseCase>();
        useCase.Setup(x => x.ExecuteAsync(request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);
        var controller = new OwnerRestaurantController(useCase.Object);

        var action = await controller.Register(request, CancellationToken.None);

        var result = action.Should().BeOfType<ObjectResult>().Subject;
        result.StatusCode.Should().Be(StatusCodes.Status201Created);
        var response = result.Value.Should().BeOfType<ApiResponse<OwnerRestaurantDto>>().Subject;
        response.Success.Should().BeTrue();
        response.Data.Should().BeSameAs(expected);
        useCase.Verify(x => x.ExecuteAsync(request, It.IsAny<CancellationToken>()), Times.Once);
    }
}
