using ANGI.Application.Common.Interfaces.UseCases.Moderation;
using ANGI.Application.DTOs.Moderation;
using ANGI.Application.Common.Models;
using ANGI.WebApi.Common.Models;
using ANGI.WebApi.Controllers.Moderation;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace ANGI.UnitTests.WebApi.Controllers.Moderation;

/// <summary>Verifies thin controller delegation and moderator role restrictions.</summary>
public sealed class ModReportsControllerTests
{
    // TEST-01: Delegate the route identifier, request and cancellation token and return the success envelope.
    /// <summary>Checks the controller preserves the use-case result and returns HTTP 200.</summary>
    [Fact]
    public async Task ReturnsUseCaseResponse()
    {
        using var cancellation = new CancellationTokenSource();
        var useCase = new Mock<IResolveReportUseCase>();
        var request = new ResolveReportRequestDto();
        var expected = new ReportDto();
        useCase.Setup(x => x.ExecuteAsync(88, request, cancellation.Token)).ReturnsAsync(expected);
        var result = (OkObjectResult)await new ModReportsController(useCase.Object, Mock.Of<IListReportsUseCase>(),
            Mock.Of<IGetReportUseCase>(), Mock.Of<IClaimReportUseCase>()).Resolve(88, request, cancellation.Token);
        result.StatusCode.Should().Be(200);
        var envelope = (ApiResponse<ReportDto>)result.Value!;
        envelope.Success.Should().BeTrue();
        envelope.Data.Should().BeSameAs(expected);
        useCase.Verify(x => x.ExecuteAsync(88, request, cancellation.Token), Times.Once);
    }

    // TEST-02: Require MOD or ADMIN at the HTTP controller boundary.
    /// <summary>Checks role authorization cannot accidentally expose moderation endpoints publicly.</summary>
    [Fact]
    public void RequiresModeratorRole()
    {
        var attribute = (AuthorizeAttribute)Attribute.GetCustomAttribute(typeof(ModReportsController), typeof(AuthorizeAttribute))!;
        attribute.Roles.Should().Be("MOD,ADMIN");
    }

    // TEST-03: Forward list query and cancellation token and preserve paging in the HTTP 200 envelope.
    /// <summary>Checks MOD-RP1 controller delegation with no query or response transformation.</summary>
    [Fact]
    public async Task ListsReports()
    {
        using var cancellation = new CancellationTokenSource();
        var list = new Mock<IListReportsUseCase>(MockBehavior.Strict);
        var request = new ListReportsRequestDto { TargetType = "restaurant" };
        var page = new Paged<ReportDto> { Items = [new() { Id = 88 }], Page = 2, PageSize = 1, TotalItems = 3, TotalPages = 3 };
        list.Setup(x => x.ExecuteAsync(request, cancellation.Token)).ReturnsAsync(page);
        var controller = new ModReportsController(Mock.Of<IResolveReportUseCase>(), list.Object,
            Mock.Of<IGetReportUseCase>(), Mock.Of<IClaimReportUseCase>());
        var result = (OkObjectResult)await controller.List(request, cancellation.Token);
        result.StatusCode.Should().Be(200);
        ((ApiResponse<Paged<ReportDto>>)result.Value!).Data.Should().BeSameAs(page);
        list.VerifyAll();
    }

    // TEST-04: Forward detail and claim identifiers and cancellation tokens without changing their results.
    /// <summary>Checks MOD-RP2 and MOD-RP3 controller delegation and success envelopes.</summary>
    [Fact]
    public async Task GetsAndClaimsReports()
    {
        using var cancellation = new CancellationTokenSource();
        var get = new Mock<IGetReportUseCase>(MockBehavior.Strict);
        var claim = new Mock<IClaimReportUseCase>(MockBehavior.Strict);
        var detail = new ReportDetailDto { Id = 88 };
        var report = new ReportDto { Id = 88, Status = "in_review" };
        get.Setup(x => x.ExecuteAsync(88, cancellation.Token)).ReturnsAsync(detail);
        claim.Setup(x => x.ExecuteAsync(88, cancellation.Token)).ReturnsAsync(report);
        var controller = new ModReportsController(Mock.Of<IResolveReportUseCase>(), Mock.Of<IListReportsUseCase>(), get.Object, claim.Object);
        var getResult = (OkObjectResult)await controller.Get(88, cancellation.Token);
        var claimResult = (OkObjectResult)await controller.Claim(88, cancellation.Token);
        ((ApiResponse<ReportDetailDto>)getResult.Value!).Data.Should().BeSameAs(detail);
        ((ApiResponse<ReportDto>)claimResult.Value!).Data.Should().BeSameAs(report);
        get.VerifyAll();
        claim.VerifyAll();
    }
}
