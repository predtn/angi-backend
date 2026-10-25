using ANGI.Application.Common.Interfaces.UseCases.Restaurant;
using ANGI.Application.DTOs.Restaurant;
using ANGI.WebApi.Common.Models;
using ANGI.WebApi.Controllers.Restaurant;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace ANGI.UnitTests.WebApi.Controllers.Restaurant;

/// <summary>Unit tests for the thin RestaurantReportsController HTTP boundary.</summary>
public sealed class RestaurantReportsControllerTests
{
    private readonly Mock<ICreateRestaurantReportUseCase> _createRestaurantReportUseCase = new();

    private RestaurantReportsController CreateController() => new(_createRestaurantReportUseCase.Object);

    [Fact]
    public void Controller_ShouldRequireTravelerRole()
    {
        var authorize = typeof(RestaurantReportsController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .Single();

        authorize.Roles.Should().Contain("TRAVELER");
    }

    [Fact]
    public async Task Create_ShouldReturnCreatedWithReportResponse()
    {
        const long restaurantId = 100;
        var request = new CreateRestaurantReportRequestDto
        {
            ReasonCode = "hygiene",
            Description = "Quán mất vệ sinh"
        };
        var expected = new CreateRestaurantReportResponseDto
        {
            ReportId = 88,
            Status = "pending"
        };
        using var source = new CancellationTokenSource();
        _createRestaurantReportUseCase
            .Setup(u => u.ExecuteAsync(restaurantId, request, source.Token))
            .ReturnsAsync(expected);

        var action = await CreateController().Create(restaurantId, request, source.Token);

        var result = action.Should().BeOfType<ObjectResult>().Subject;
        result.StatusCode.Should().Be(201);
        result.Value.Should().BeOfType<ApiResponse<CreateRestaurantReportResponseDto>>()
            .Which.Data.Should().BeSameAs(expected);
        _createRestaurantReportUseCase.Verify(u => u.ExecuteAsync(restaurantId, request, source.Token), Times.Once);
    }
}
