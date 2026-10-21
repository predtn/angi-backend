using ANGI.Application.Common.Interfaces.UseCases.Social;
using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Social;
using ANGI.WebApi.Common.Models;
using ANGI.WebApi.Controllers.Social;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace ANGI.UnitTests.WebApi.Controllers.Social;

/// <summary>Unit tests for the thin BLOG-01 and BLOG-04 HTTP boundaries.</summary>
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
        var getPublicBlogsUseCase = new Mock<IGetPublicBlogsUseCase>();
        var useCase = new Mock<ICreateBlogUseCase>();
        using var source = new CancellationTokenSource();
        useCase.Setup(item => item.ExecuteAsync(request, source.Token)).ReturnsAsync(expected);

        var action = await new BlogsController(getPublicBlogsUseCase.Object, useCase.Object).Create(request, source.Token);

        var result = action.Should().BeOfType<ObjectResult>().Subject;
        result.StatusCode.Should().Be(201);
        result.Value.Should().BeOfType<ApiResponse<BlogDetailDto>>().Which.Data.Should().BeSameAs(expected);
        useCase.Verify(item => item.ExecuteAsync(request, source.Token), Times.Once);
    }

    // TEST-02: Allow anonymous callers on BLOG-01 and return the paged blog list in a 200 ApiResponse.
    /// <summary>Verifies AllowAnonymous metadata, delegation, cancellation, and response wrapping for BLOG-01.</summary>
    [Fact]
    public async Task GetPublicBlogs_ShouldAllowAnonymousAndReturnOkResponse()
    {
        var method = typeof(BlogsController).GetMethod(nameof(BlogsController.GetPublicBlogs))!;
        method.GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: true).Should().ContainSingle();

        var request = new GetPublicBlogsRequestDto { Q = "Đà Nẵng", Page = 1, PageSize = 20 };
        var expected = new Paged<BlogCardDto>
        {
            Items = [new BlogCardDto { Id = 45, Title = "Hành trình 3 ngày ăn ở Đà Nẵng" }],
            Page = 1,
            PageSize = 20,
            TotalItems = 1,
            TotalPages = 1
        };
        var getPublicBlogsUseCase = new Mock<IGetPublicBlogsUseCase>();
        var createBlogUseCase = new Mock<ICreateBlogUseCase>();
        using var source = new CancellationTokenSource();
        getPublicBlogsUseCase.Setup(item => item.ExecuteAsync(request, source.Token)).ReturnsAsync(expected);

        var action = await new BlogsController(getPublicBlogsUseCase.Object, createBlogUseCase.Object)
            .GetPublicBlogs(request, source.Token);

        var result = action.Should().BeOfType<ObjectResult>().Subject;
        result.StatusCode.Should().Be(200);
        var response = result.Value.Should().BeOfType<ApiResponse<Paged<BlogCardDto>>>().Subject;
        response.Success.Should().BeTrue();
        response.Data.Should().BeSameAs(expected);
        getPublicBlogsUseCase.Verify(item => item.ExecuteAsync(request, source.Token), Times.Once);
    }
}
