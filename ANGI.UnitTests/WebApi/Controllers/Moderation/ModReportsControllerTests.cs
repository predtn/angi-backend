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
        // STEP-01: Create cancellation as CancellationTokenSource.
        using var cancellation = new CancellationTokenSource();
        // STEP-02: Mock IResolveReportUseCase.
        var useCase = new Mock<IResolveReportUseCase>();
        // STEP-03: Create request as ResolveReportRequestDto and create expected as ReportDto.
        var request = new ResolveReportRequestDto();
        var expected = new ReportDto();
        // STEP-04: Stub useCase.ExecuteAsync.
        useCase.Setup(x => x.ExecuteAsync(88, request, cancellation.Token)).ReturnsAsync(expected);
        // STEP-05: Derive result from new ModReportsController().Resolve().
        var result = (OkObjectResult)await new ModReportsController(useCase.Object, Mock.Of<IListReportsUseCase>(),
            Mock.Of<IGetReportUseCase>(), Mock.Of<IClaimReportUseCase>()).Resolve(88, request, cancellation.Token);
        // STEP-06: Assert result.StatusCode is 200.
        result.StatusCode.Should().Be(200);
        // STEP-07: Derive envelope from result.Value.
        var envelope = (ApiResponse<ReportDto>)result.Value!;
        // STEP-08: Assert envelope.Success is true and envelope.Data is the same instance as expected.
        envelope.Success.Should().BeTrue();
        envelope.Data.Should().BeSameAs(expected);
        // STEP-09: Verify ExecuteAsync is called once.
        useCase.Verify(x => x.ExecuteAsync(88, request, cancellation.Token), Times.Once);
    }

    // TEST-02: Require MOD or ADMIN at the HTTP controller boundary.
    /// <summary>Checks role authorization cannot accidentally expose moderation endpoints publicly.</summary>
    [Fact]
    public void RequiresModeratorRole()
    {
        // STEP-01: Derive attribute from Attribute.GetCustomAttribute().
        var attribute = (AuthorizeAttribute)Attribute.GetCustomAttribute(typeof(ModReportsController), typeof(AuthorizeAttribute))!;
        // STEP-02: Assert attribute.Roles is "MOD,ADMIN".
        attribute.Roles.Should().Be("MOD,ADMIN");
    }

    // TEST-03: Forward list query and cancellation token and preserve paging in the HTTP 200 envelope.
    /// <summary>Checks MOD-RP1 controller delegation with no query or response transformation.</summary>
    [Fact]
    public async Task ListsReports()
    {
        // STEP-01: Create cancellation as CancellationTokenSource.
        using var cancellation = new CancellationTokenSource();
        // STEP-02: Mock IListReportsUseCase.
        var list = new Mock<IListReportsUseCase>(MockBehavior.Strict);
        // STEP-03: Create request as ListReportsRequestDto and create page as Paged<ReportDto>.
        var request = new ListReportsRequestDto { TargetType = "restaurant" };
        var page = new Paged<ReportDto> { Items = [new() { Id = 88 }], Page = 2, PageSize = 1, TotalItems = 3, TotalPages = 3 };
        // STEP-04: Stub list.ExecuteAsync.
        list.Setup(x => x.ExecuteAsync(request, cancellation.Token)).ReturnsAsync(page);
        // STEP-05: Create the ModReportsController under test as controller.
        var controller = new ModReportsController(Mock.Of<IResolveReportUseCase>(), list.Object,
            Mock.Of<IGetReportUseCase>(), Mock.Of<IClaimReportUseCase>());
        // STEP-06: Run controller.List with request and keep result.
        var result = (OkObjectResult)await controller.List(request, cancellation.Token);
        // STEP-07: Assert result.StatusCode is 200 and result.Value.Data is the same instance as page.
        result.StatusCode.Should().Be(200);
        ((ApiResponse<Paged<ReportDto>>)result.Value!).Data.Should().BeSameAs(page);
        // STEP-08: Verify every setup on list ran.
        list.VerifyAll();
    }

    // TEST-04: Forward detail and claim identifiers and cancellation tokens without changing their results.
    /// <summary>Checks MOD-RP2 and MOD-RP3 controller delegation and success envelopes.</summary>
    [Fact]
    public async Task GetsAndClaimsReports()
    {
        // STEP-01: Create cancellation as CancellationTokenSource.
        using var cancellation = new CancellationTokenSource();
        // STEP-02: Mock IGetReportUseCase and IClaimReportUseCase.
        var get = new Mock<IGetReportUseCase>(MockBehavior.Strict);
        var claim = new Mock<IClaimReportUseCase>(MockBehavior.Strict);
        // STEP-03: Create detail as ReportDetailDto and create report as ReportDto.
        var detail = new ReportDetailDto { Id = 88 };
        var report = new ReportDto { Id = 88, Status = "in_review" };
        // STEP-04: Stub get.ExecuteAsync and claim.ExecuteAsync.
        get.Setup(x => x.ExecuteAsync(88, cancellation.Token)).ReturnsAsync(detail);
        claim.Setup(x => x.ExecuteAsync(88, cancellation.Token)).ReturnsAsync(report);
        // STEP-05: Create the ModReportsController under test as controller.
        var controller = new ModReportsController(Mock.Of<IResolveReportUseCase>(), Mock.Of<IListReportsUseCase>(), get.Object, claim.Object);
        // STEP-06: Run controller.Get and keep getResult.
        var getResult = (OkObjectResult)await controller.Get(88, cancellation.Token);
        // STEP-07: Run controller.Claim and keep claimResult.
        var claimResult = (OkObjectResult)await controller.Claim(88, cancellation.Token);
        // STEP-08: Assert getResult.Value.Data is the same instance as detail and claimResult.Value.Data is the same instance as report.
        ((ApiResponse<ReportDetailDto>)getResult.Value!).Data.Should().BeSameAs(detail);
        ((ApiResponse<ReportDto>)claimResult.Value!).Data.Should().BeSameAs(report);
        // STEP-09: Verify every setup on get ran and every setup on claim ran.
        get.VerifyAll();
        claim.VerifyAll();
    }
}
