using ANGI.Application.Common.Interfaces.UseCases.Roadmap;
using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Roadmap;
using ANGI.WebApi.Common.Models;
using ANGI.WebApi.Controllers.Roadmap;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace ANGI.UnitTests.WebApi.Controllers.Roadmap;

public sealed class RoadmapsControllerTests
{
    // TEST-01: Return the RM-01 payload in ApiResponse with HTTP 201.
    /// <summary>Verifies create delegation and the documented success envelope.</summary>
    [Fact]
    public async Task Create_WithValidRequest_ShouldReturnCreatedApiResponse()
    {
        var request = new CreateRoadmapRequestDto { Title = "Trip", NumDays = 3 };
        var expected = new RoadmapDetailDto { Overview = new RoadmapSummaryDto { Id = 88 } };
        var useCase = new Mock<ICreateRoadmapUseCase>();
        useCase.Setup(item => item.ExecuteAsync(request, It.IsAny<CancellationToken>())).ReturnsAsync(expected);
        var controller = new RoadmapsController(
            useCase.Object,
            Mock.Of<IGetMyRoadmapsUseCase>(),
            Mock.Of<IGetRoadmapDetailUseCase>(),
            Mock.Of<IGetRoadmapDayRouteUseCase>(),
            Mock.Of<IUpdateRoadmapUseCase>());

        var action = await controller.Create(request, CancellationToken.None);

        var result = action.Should().BeOfType<ObjectResult>().Subject;
        result.StatusCode.Should().Be(StatusCodes.Status201Created);
        result.Value.Should().BeOfType<ApiResponse<RoadmapDetailDto>>().Which.Data.Should().BeSameAs(expected);
    }

    // TEST-02: Return the RM-04 payload in ApiResponse with HTTP 200.
    /// <summary>Verifies update route-value delegation and the documented success envelope.</summary>
    [Fact]
    public async Task Update_WithValidRequest_ShouldReturnOkApiResponse()
    {
        var request = new UpdateRoadmapRequestDto { Title = "Updated trip" };
        var expected = new RoadmapDetailDto { Overview = new RoadmapSummaryDto { Id = 88 } };
        var useCase = new Mock<IUpdateRoadmapUseCase>();
        useCase.Setup(item => item.ExecuteAsync(88, request, It.IsAny<CancellationToken>())).ReturnsAsync(expected);
        var controller = new RoadmapsController(
            Mock.Of<ICreateRoadmapUseCase>(),
            Mock.Of<IGetMyRoadmapsUseCase>(),
            Mock.Of<IGetRoadmapDetailUseCase>(),
            Mock.Of<IGetRoadmapDayRouteUseCase>(),
            useCase.Object);

        var action = await controller.Update(88, request, CancellationToken.None);

        var result = action.Should().BeOfType<OkObjectResult>().Subject;
        result.Value.Should().BeOfType<ApiResponse<RoadmapDetailDto>>().Which.Data.Should().BeSameAs(expected);
    }

    // TEST-03: Return the RM-02 paged payload in ApiResponse with HTTP 200.
    /// <summary>Verifies query-model delegation and the documented list success envelope.</summary>
    [Fact]
    public async Task GetMyRoadmaps_WithValidQuery_ShouldReturnOkApiResponse()
    {
        var request = new GetMyRoadmapsRequestDto { Page = 2, PageSize = 10 };
        var expected = new Paged<RoadmapSummaryDto>
        {
            Page = 2,
            PageSize = 10,
            TotalItems = 11,
            TotalPages = 2
        };
        var useCase = new Mock<IGetMyRoadmapsUseCase>();
        useCase.Setup(item => item.ExecuteAsync(request, It.IsAny<CancellationToken>())).ReturnsAsync(expected);
        var controller = new RoadmapsController(
            Mock.Of<ICreateRoadmapUseCase>(),
            useCase.Object,
            Mock.Of<IGetRoadmapDetailUseCase>(),
            Mock.Of<IGetRoadmapDayRouteUseCase>(),
            Mock.Of<IUpdateRoadmapUseCase>());

        var action = await controller.GetMyRoadmaps(request, CancellationToken.None);

        var result = action.Should().BeOfType<OkObjectResult>().Subject;
        result.Value.Should().BeOfType<ApiResponse<Paged<RoadmapSummaryDto>>>()
            .Which.Data.Should().BeSameAs(expected);
    }

    // TEST-04: Return the RM-03 detail payload in ApiResponse with HTTP 200.
    /// <summary>Verifies detail route-value delegation and the documented success envelope.</summary>
    [Fact]
    public async Task GetDetail_WithOwnedRoadmap_ShouldReturnOkApiResponse()
    {
        var expected = new RoadmapDetailDto { Overview = new RoadmapSummaryDto { Id = 88 } };
        var useCase = new Mock<IGetRoadmapDetailUseCase>();
        useCase.Setup(item => item.ExecuteAsync(88, It.IsAny<CancellationToken>())).ReturnsAsync(expected);
        var controller = new RoadmapsController(
            Mock.Of<ICreateRoadmapUseCase>(),
            Mock.Of<IGetMyRoadmapsUseCase>(),
            useCase.Object,
            Mock.Of<IGetRoadmapDayRouteUseCase>(),
            Mock.Of<IUpdateRoadmapUseCase>());

        var action = await controller.GetDetail(88, CancellationToken.None);

        var result = action.Should().BeOfType<OkObjectResult>().Subject;
        result.Value.Should().BeOfType<ApiResponse<RoadmapDetailDto>>().Which.Data.Should().BeSameAs(expected);
    }

    // TEST-05: Return the RM-14 daily route payload in ApiResponse with HTTP 200.
    /// <summary>Verifies both route values, query delegation, and the documented success envelope.</summary>
    [Fact]
    public async Task GetDayRoute_WithValidQuery_ShouldReturnOkApiResponse()
    {
        var request = new GetRoadmapDayRouteRequestDto { Profile = "walking" };
        var expected = new GetRoadmapDayRouteResponseDto { DayId = 301, Profile = "walking" };
        var useCase = new Mock<IGetRoadmapDayRouteUseCase>();
        useCase.Setup(item => item.ExecuteAsync(88, 301, request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);
        var controller = new RoadmapsController(
            Mock.Of<ICreateRoadmapUseCase>(),
            Mock.Of<IGetMyRoadmapsUseCase>(),
            Mock.Of<IGetRoadmapDetailUseCase>(),
            useCase.Object,
            Mock.Of<IUpdateRoadmapUseCase>());

        var action = await controller.GetDayRoute(88, 301, request, CancellationToken.None);

        var result = action.Should().BeOfType<OkObjectResult>().Subject;
        result.Value.Should().BeOfType<ApiResponse<GetRoadmapDayRouteResponseDto>>().Which.Data.Should().BeSameAs(expected);
    }
}
