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

        var deleteUseCase = new Mock<IDeleteBlogUseCase>();
        var action = await new BlogsController(useCase.Object, deleteUseCase.Object).Create(request, source.Token);

        var result = action.Should().BeOfType<ObjectResult>().Subject;
        result.StatusCode.Should().Be(201);
        result.Value.Should().BeOfType<ApiResponse<BlogDetailDto>>().Which.Data.Should().BeSameAs(expected);
        useCase.Verify(item => item.ExecuteAsync(request, source.Token), Times.Once);
    }

    // TEST-02: Require the Traveler role and return a 200 ApiResponse with null data on soft deletion.
    /// <summary>Verifies BLOG-07 metadata, delegation, and empty response wrapping.</summary>
    [Fact]
    public async Task Delete_ShouldRequireTravelerAndReturnOkResponse()
    {
        var method = typeof(BlogsController).GetMethod(nameof(BlogsController.Delete))!;
        var httpDelete = method
            .GetCustomAttributes(typeof(HttpDeleteAttribute), inherit: false)
            .Cast<HttpDeleteAttribute>()
            .Single();
        httpDelete.Template.Should().Be("{blogId:long}");

        var authorize = method
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>()
            .Single();
        authorize.Roles.Should().Contain("TRAVELER");

        var responseTypes = method
            .GetCustomAttributes(typeof(ProducesResponseTypeAttribute), inherit: false)
            .Cast<ProducesResponseTypeAttribute>()
            .Select(attr => attr.StatusCode)
            .ToHashSet();
        responseTypes.Should().Contain([200, 400, 401, 403, 404]);

        var createUseCase = new Mock<ICreateBlogUseCase>();
        var deleteUseCase = new Mock<IDeleteBlogUseCase>();
        using var source = new CancellationTokenSource();
        deleteUseCase.Setup(item => item.ExecuteAsync(45, source.Token)).Returns(Task.CompletedTask);

        var controller = new BlogsController(createUseCase.Object, deleteUseCase.Object);
        var action = await controller.Delete(45, source.Token);

        var result = action.Should().BeOfType<OkObjectResult>().Subject;
        result.StatusCode.Should().Be(200);
        var response = result.Value.Should().BeOfType<ApiResponse<object>>().Subject;
        response.Success.Should().BeTrue();
        response.Data.Should().BeNull();
        deleteUseCase.Verify(item => item.ExecuteAsync(45, source.Token), Times.Once);
    }
}
