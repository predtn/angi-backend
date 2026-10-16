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
            Mock.Of<IUpdateRoadmapUseCase>());

        var action = await controller.GetMyRoadmaps(request, CancellationToken.None);

        var result = action.Should().BeOfType<OkObjectResult>().Subject;
        result.Value.Should().BeOfType<ApiResponse<Paged<RoadmapSummaryDto>>>()
            .Which.Data.Should().BeSameAs(expected);
    }
}
