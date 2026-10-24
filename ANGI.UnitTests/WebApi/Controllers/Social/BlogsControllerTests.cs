using ANGI.Application.Common.Interfaces.UseCases.Social;
using ANGI.Application.DTOs.Social;
using ANGI.WebApi.Common.Models;
using ANGI.WebApi.Controllers.Social;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace ANGI.UnitTests.WebApi.Controllers.Social;

/// <summary>Unit tests for the thin BLOG-04 HTTP boundary.</summary>
public sealed class BlogsControllerTests
{
    // TEST-01: Require the Traveler role and return the BLOG-04 payload in a 201 ApiResponse.
    /// <summary>Verifies authorization metadata, delegation, cancellation, and response wrapping.</summary>
    [Fact]
    public async Task Create_ShouldRequireTravelerAndReturnCreatedResponse()
    {
        var authorize = typeof(BlogsController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .Single();
        authorize.Roles.Should().Be("TRAVELER");
        var expected = new BlogDetailDto { Id = 45, Title = "Chuyến đi" };
        var request = new CreateBlogRequestDto { Title = "Chuyến đi", Content = "Nội dung" };
        var useCase = new Mock<ICreateBlogUseCase>();
        using var source = new CancellationTokenSource();
        useCase.Setup(item => item.ExecuteAsync(request, source.Token)).ReturnsAsync(expected);

        var action = await new BlogsController(useCase.Object).Create(request, source.Token);

        var result = action.Should().BeOfType<ObjectResult>().Subject;
        result.StatusCode.Should().Be(201);
        result.Value.Should().BeOfType<ApiResponse<BlogDetailDto>>().Which.Data.Should().BeSameAs(expected);
        useCase.Verify(item => item.ExecuteAsync(request, source.Token), Times.Once);
    }
}
