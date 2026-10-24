using ANGI.Application.Common.Interfaces.UseCases.Social;
using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Social;
using ANGI.WebApi.Common.Models;
using ANGI.WebApi.Controllers.Social;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace ANGI.UnitTests.WebApi.Controllers.Social;

/// <summary>Unit tests for the BLOG-05 HTTP boundary.</summary>
public sealed class MeBlogControllerTests
{
    // TEST-01: Require TRAVELER role on MeBlogController.
    [Fact]
    public void Controller_ShouldRequireTravelerRole()
    {
        var authorize = typeof(MeBlogController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .Single();

        authorize.Roles.Should().Be("TRAVELER");
    }

    // TEST-02: Delegate to use case and return 200 OK wrapped in ApiResponse.
    [Fact]
    public async Task GetMyBlogs_ShouldDelegateAndReturnOkResponse()
    {
        var expected = new Paged<MyBlogCardDto>
        {
            Items = [new MyBlogCardDto { Id = 45, Title = "Đà Nẵng", Status = "published" }],
            Page = 1,
            PageSize = 20,
            TotalItems = 1,
            TotalPages = 1
        };

        var request = new GetMyBlogsRequestDto { Page = 1, PageSize = 20 };
        var useCase = new Mock<IGetMyBlogsUseCase>();
        using var source = new CancellationTokenSource();
        useCase.Setup(item => item.ExecuteAsync(request, source.Token)).ReturnsAsync(expected);

        var controller = new MeBlogController(useCase.Object);
        var action = await controller.GetMyBlogs(request, source.Token);

        var okResult = action.Should().BeOfType<OkObjectResult>().Subject;
        okResult.StatusCode.Should().Be(StatusCodes.Status200OK);
        var envelope = okResult.Value.Should().BeOfType<ApiResponse<Paged<MyBlogCardDto>>>().Subject;
        envelope.Success.Should().BeTrue();
        envelope.Data.Should().BeSameAs(expected);

        useCase.Verify(item => item.ExecuteAsync(request, source.Token), Times.Once);
    }
}
