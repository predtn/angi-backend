using ANGI.Application.Common.Interfaces.UseCases.Social;
using ANGI.Application.DTOs.Social;
using ANGI.WebApi.Common.Models;
using ANGI.WebApi.Controllers.Social;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace ANGI.UnitTests.WebApi.Controllers.Social;

/// <summary>Unit tests for the thin BlogsController HTTP boundary.</summary>
public sealed class BlogsControllerTests
{
    private readonly Mock<ICreateBlogUseCase> _createBlogUseCase = new();
    private readonly Mock<ILikeBlogUseCase> _likeBlogUseCase = new();
    private readonly Mock<IUnlikeBlogUseCase> _unlikeBlogUseCase = new();
    private readonly Mock<ICreateBlogCommentUseCase> _createBlogCommentUseCase = new();

    private BlogsController CreateController() =>
        new(
            _createBlogUseCase.Object,
            _likeBlogUseCase.Object,
            _unlikeBlogUseCase.Object,
            _createBlogCommentUseCase.Object);

    [Fact]
    public void Controller_ShouldRequireTravelerRole()
    {
        var authorize = typeof(BlogsController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .Single();
        authorize.Roles.Should().Be("TRAVELER");
    }

    [Fact]
    public async Task Create_ShouldReturnCreatedResponse()
    {
        var expected = new BlogDetailDto { Id = 45, Title = "Chuyến đi" };
        var request = new CreateBlogRequestDto { Title = "Chuyến đi", Content = "Nội dung" };
        using var source = new CancellationTokenSource();
        _createBlogUseCase.Setup(item => item.ExecuteAsync(request, source.Token)).ReturnsAsync(expected);

        var action = await CreateController().Create(request, source.Token);

        var result = action.Should().BeOfType<ObjectResult>().Subject;
        result.StatusCode.Should().Be(201);
        result.Value.Should().BeOfType<ApiResponse<BlogDetailDto>>().Which.Data.Should().BeSameAs(expected);
        _createBlogUseCase.Verify(item => item.ExecuteAsync(request, source.Token), Times.Once);
    }

    [Fact]
    public async Task Like_ShouldReturnOkWithLikeResponse()
    {
        const long blogId = 45;
        var expected = new BlogLikeResponseDto { Liked = true, LikeCount = 10 };
        using var source = new CancellationTokenSource();
        _likeBlogUseCase.Setup(item => item.ExecuteAsync(blogId, source.Token)).ReturnsAsync(expected);

        var action = await CreateController().Like(blogId, source.Token);

        var result = action.Should().BeOfType<OkObjectResult>().Subject;
        result.StatusCode.Should().Be(200);
        result.Value.Should().BeOfType<ApiResponse<BlogLikeResponseDto>>().Which.Data.Should().BeSameAs(expected);
        _likeBlogUseCase.Verify(item => item.ExecuteAsync(blogId, source.Token), Times.Once);
    }

    [Fact]
    public async Task Unlike_ShouldReturnOkWithLikeResponse()
    {
        const long blogId = 45;
        var expected = new BlogLikeResponseDto { Liked = false, LikeCount = 9 };
        using var source = new CancellationTokenSource();
        _unlikeBlogUseCase.Setup(item => item.ExecuteAsync(blogId, source.Token)).ReturnsAsync(expected);

        var action = await CreateController().Unlike(blogId, source.Token);

        var result = action.Should().BeOfType<OkObjectResult>().Subject;
        result.StatusCode.Should().Be(200);
        result.Value.Should().BeOfType<ApiResponse<BlogLikeResponseDto>>().Which.Data.Should().BeSameAs(expected);
        _unlikeBlogUseCase.Verify(item => item.ExecuteAsync(blogId, source.Token), Times.Once);
    }

    [Fact]
    public async Task Comment_ShouldReturnCreatedWithCommentResponse()
    {
        const long blogId = 45;
        var request = new CreateBlogCommentRequestDto { Content = "Quán ngon" };
        var expected = new CommentDto { Id = 302, BlogId = blogId, Content = "Quán ngon" };
        using var source = new CancellationTokenSource();
        _createBlogCommentUseCase.Setup(item => item.ExecuteAsync(blogId, request, source.Token)).ReturnsAsync(expected);

        var action = await CreateController().Comment(blogId, request, source.Token);

        var result = action.Should().BeOfType<ObjectResult>().Subject;
        result.StatusCode.Should().Be(201);
        result.Value.Should().BeOfType<ApiResponse<CommentDto>>().Which.Data.Should().BeSameAs(expected);
        _createBlogCommentUseCase.Verify(item => item.ExecuteAsync(blogId, request, source.Token), Times.Once);
    }
}
