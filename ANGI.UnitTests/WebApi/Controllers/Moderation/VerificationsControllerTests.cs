using ANGI.Application.Common.Interfaces.UseCases.Moderation;
using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Moderation;
using ANGI.Application.DTOs.Restaurant;
using ANGI.WebApi.Common.Models;
using ANGI.WebApi.Controllers.Moderation;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace ANGI.UnitTests.WebApi.Controllers.Moderation;

public sealed class VerificationsControllerTests
{
    private readonly Mock<IListVerificationsUseCase> _list = new();
    private readonly Mock<IGetVerificationReviewUseCase> _get = new();
    private readonly Mock<IReviewVerificationUseCase> _review = new();

    // TEST-01: Require MOD or ADMIN role authorization at the controller boundary.
    /// <summary>Verifies all MOD-V endpoints inherit the documented role restriction.</summary>
    [Fact]
    public void Controller_ShouldRequireModOrAdminRole()
    {
        var authorize = typeof(VerificationsController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), true)
            .Cast<AuthorizeAttribute>()
            .Single();

        authorize.Roles.Should().Be("MOD,ADMIN");
    }

    // TEST-02: Return the MOD-V1 page inside the standard success envelope.
    /// <summary>Verifies the thin list action delegates once and returns HTTP 200.</summary>
    [Fact]
    public async Task List_ShouldReturnOkEnvelope()
    {
        var request = new ListVerificationsRequestDto();
        var data = new Paged<ModVerificationCardDto>
        {
            Items = [new ModVerificationCardDto { Id = 21 }],
            Page = 1,
            PageSize = 20,
            TotalItems = 1,
            TotalPages = 1
        };
        _list.Setup(item => item.ExecuteAsync(request, It.IsAny<CancellationToken>())).ReturnsAsync(data);

        var result = await Controller().List(request, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.StatusCode.Should().Be(StatusCodes.Status200OK);
        ok.Value.Should().BeOfType<ApiResponse<Paged<ModVerificationCardDto>>>()
            .Which.Data.Should().BeSameAs(data);
        _list.Verify(item => item.ExecuteAsync(request, It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-03: Return flattened MOD-V2 details inside the standard success envelope.
    /// <summary>Verifies the thin detail action forwards the route id and returns HTTP 200.</summary>
    [Fact]
    public async Task Get_ShouldReturnOkEnvelope()
    {
        var data = new VerificationReviewDetailDto { Id = 21 };
        _get.Setup(item => item.ExecuteAsync(21, It.IsAny<CancellationToken>())).ReturnsAsync(data);

        var result = await Controller().Get(21, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeOfType<ApiResponse<VerificationReviewDetailDto>>()
            .Which.Data.Should().BeSameAs(data);
        _get.Verify(item => item.ExecuteAsync(21, It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-04: Return the MOD-V3 verification inside the standard success envelope.
    /// <summary>Verifies the thin decision action forwards route, body, and cancellation token.</summary>
    [Fact]
    public async Task Decide_ShouldReturnOkEnvelope()
    {
        var request = new ReviewVerificationRequestDto { Decision = "approve" };
        var data = new VerificationDto { Id = 21, Status = "approved" };
        _review.Setup(item => item.ExecuteAsync(21, request, It.IsAny<CancellationToken>())).ReturnsAsync(data);

        var result = await Controller().Decide(21, request, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeOfType<ApiResponse<VerificationDto>>()
            .Which.Data.Should().BeSameAs(data);
        _review.Verify(item => item.ExecuteAsync(21, request, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>Creates a controller backed by the three isolated use-case mocks.</summary>
    private VerificationsController Controller() => new(_list.Object, _get.Object, _review.Object);
}
