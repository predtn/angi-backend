using ANGI.Application.Common.Interfaces.UseCases.Administration;
using ANGI.Application.DTOs.Administration;
using ANGI.WebApi.Common.Models;
using ANGI.WebApi.Controllers.Administration;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace ANGI.UnitTests.WebApi.Controllers.Administration;

public sealed class AdminDashboardControllerTests
{
    [Fact]
    public async Task GetSummary_ShouldReturnOkApiResponse()
    {
        var request = new GetDashboardSummaryRequestDto();
        var expected = new DashboardSummaryDto();
        var useCase = new Mock<IGetDashboardSummaryUseCase>();
        useCase.Setup(item => item.ExecuteAsync(request, It.IsAny<CancellationToken>())).ReturnsAsync(expected);
        var controller = new AdminDashboardController(useCase.Object, Mock.Of<IGetDashboardTimeSeriesUseCase>(), Mock.Of<IGetDashboardTopUseCase>());

        var action = await controller.GetSummary(request, CancellationToken.None);

        var result = action.Should().BeOfType<OkObjectResult>().Subject;
        result.Value.Should().BeOfType<ApiResponse<DashboardSummaryDto>>().Which.Data.Should().BeSameAs(expected);
    }

    [Fact]
    public async Task GetTimeSeries_ShouldReturnOkApiResponse()
    {
        var request = new GetDashboardTimeSeriesRequestDto();
        var expected = new DashboardTimeSeriesDto();
        var useCase = new Mock<IGetDashboardTimeSeriesUseCase>();
        useCase.Setup(item => item.ExecuteAsync(request, It.IsAny<CancellationToken>())).ReturnsAsync(expected);
        var controller = new AdminDashboardController(Mock.Of<IGetDashboardSummaryUseCase>(), useCase.Object, Mock.Of<IGetDashboardTopUseCase>());

        var action = await controller.GetTimeSeries(request, CancellationToken.None);

        var result = action.Should().BeOfType<OkObjectResult>().Subject;
        result.Value.Should().BeOfType<ApiResponse<DashboardTimeSeriesDto>>().Which.Data.Should().BeSameAs(expected);
    }

    [Fact]
    public async Task GetTop_ShouldReturnOkApiResponse()
    {
        var request = new GetDashboardTopRequestDto();
        var expected = new DashboardTopDto();
        var useCase = new Mock<IGetDashboardTopUseCase>();
        useCase.Setup(item => item.ExecuteAsync(request, It.IsAny<CancellationToken>())).ReturnsAsync(expected);
        var controller = new AdminDashboardController(Mock.Of<IGetDashboardSummaryUseCase>(), Mock.Of<IGetDashboardTimeSeriesUseCase>(), useCase.Object);

        var action = await controller.GetTop(request, CancellationToken.None);

        var result = action.Should().BeOfType<OkObjectResult>().Subject;
        result.Value.Should().BeOfType<ApiResponse<DashboardTopDto>>().Which.Data.Should().BeSameAs(expected);
    }
}
