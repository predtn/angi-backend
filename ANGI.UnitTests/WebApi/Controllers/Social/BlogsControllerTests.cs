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

/// <summary>Unit tests for the thin BLOG-01, BLOG-02, BLOG-03, BLOG-04, BLOG-06, BLOG-07, BLOG-08, BLOG-09, and BLOG-10 HTTP boundaries.</summary>
public sealed class BlogsControllerTests
{
    private readonly Mock<ICreateBlogUseCase> _createBlogUseCase = new();
    private readonly Mock<ILikeBlogUseCase> _likeBlogUseCase = new();
    private readonly Mock<IUnlikeBlogUseCase> _unlikeBlogUseCase = new();
    private readonly Mock<ICreateBlogCommentUseCase> _createBlogCommentUseCase = new();

    private BlogsController CreateController() =>
        new(
            Mock.Of<IGetPublicBlogsUseCase>(),
            _createBlogUseCase.Object,
            Mock.Of<IGetBlogDetailUseCase>(),
            Mock.Of<IGetBlogCommentsUseCase>(),
            Mock.Of<IEditBlogUseCase>(),
            Mock.Of<IDeleteBlogUseCase>(),
            _likeBlogUseCase.Object,
            _unlikeBlogUseCase.Object,
            _createBlogCommentUseCase.Object);

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

        var action = await new BlogsController(
            getPublicBlogsUseCase.Object,
            useCase.Object,
            Mock.Of<IGetBlogDetailUseCase>(),
            Mock.Of<IGetBlogCommentsUseCase>(),
            Mock.Of<IEditBlogUseCase>(),
            Mock.Of<IDeleteBlogUseCase>(),
            Mock.Of<ILikeBlogUseCase>(),
            Mock.Of<IUnlikeBlogUseCase>(),
            Mock.Of<ICreateBlogCommentUseCase>()).Create(request, source.Token);

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

        var action = await new BlogsController(
            getPublicBlogsUseCase.Object,
            createBlogUseCase.Object,
            Mock.Of<IGetBlogDetailUseCase>(),
            Mock.Of<IGetBlogCommentsUseCase>(),
            Mock.Of<IEditBlogUseCase>(),
            Mock.Of<IDeleteBlogUseCase>(),
            Mock.Of<ILikeBlogUseCase>(),
            Mock.Of<IUnlikeBlogUseCase>(),
            Mock.Of<ICreateBlogCommentUseCase>())
            .GetPublicBlogs(request, source.Token);

        var result = action.Should().BeOfType<ObjectResult>().Subject;
        result.StatusCode.Should().Be(200);
        var response = result.Value.Should().BeOfType<ApiResponse<Paged<BlogCardDto>>>().Subject;
        response.Success.Should().BeTrue();
        response.Data.Should().BeSameAs(expected);
        getPublicBlogsUseCase.Verify(item => item.ExecuteAsync(request, source.Token), Times.Once);
    }

    // TEST-03: Allow anonymous callers on BLOG-02 and return a 200 ApiResponse<BlogDetailDto>.
    [Fact]
    public async Task GetDetail_ShouldAllowAnonymousAndReturnOkResponse()
    {
        var method = typeof(BlogsController).GetMethod(nameof(BlogsController.GetDetail))!;
        method.GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: true).Should().ContainSingle();

        var expected = new BlogDetailDto { Id = 45, Title = "Hành trình 3 ngày ăn ở Đà Nẵng" };
        var useCase = new Mock<IGetBlogDetailUseCase>();
        using var source = new CancellationTokenSource();
        useCase.Setup(item => item.ExecuteAsync(45, source.Token)).ReturnsAsync(expected);

        var controller = new BlogsController(
            Mock.Of<IGetPublicBlogsUseCase>(),
            Mock.Of<ICreateBlogUseCase>(),
            useCase.Object,
            Mock.Of<IGetBlogCommentsUseCase>(),
            Mock.Of<IEditBlogUseCase>(),
            Mock.Of<IDeleteBlogUseCase>(),
            Mock.Of<ILikeBlogUseCase>(),
            Mock.Of<IUnlikeBlogUseCase>(),
            Mock.Of<ICreateBlogCommentUseCase>());
        var action = await controller.GetDetail(45, source.Token);

        var result = action.Should().BeOfType<OkObjectResult>().Subject;
        result.StatusCode.Should().Be(StatusCodes.Status200OK);
        var response = result.Value.Should().BeOfType<ApiResponse<BlogDetailDto>>().Subject;
        response.Success.Should().BeTrue();
        response.Data.Should().BeSameAs(expected);
        useCase.Verify(item => item.ExecuteAsync(45, source.Token), Times.Once);
    }

    // TEST-04: Allow anonymous callers on BLOG-03 and return a 200 ApiResponse<Paged<CommentDto>>.
    [Fact]
    public async Task GetComments_ShouldAllowAnonymousAndReturnOkResponse()
    {
        var method = typeof(BlogsController).GetMethod(nameof(BlogsController.GetComments))!;
        method.GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: true).Should().ContainSingle();

        var request = new GetBlogCommentsRequestDto { Page = 1, PageSize = 20 };
        var expected = new Paged<CommentDto>
        {
            Items = [new CommentDto { Id = 302, BlogId = 45, Content = "Quán này ngon thật!" }],
            Page = 1,
            PageSize = 20,
            TotalItems = 1,
            TotalPages = 1
        };
        var useCase = new Mock<IGetBlogCommentsUseCase>();
        using var source = new CancellationTokenSource();
        useCase.Setup(item => item.ExecuteAsync(45, request, source.Token)).ReturnsAsync(expected);

        var controller = new BlogsController(
            Mock.Of<IGetPublicBlogsUseCase>(),
            Mock.Of<ICreateBlogUseCase>(),
            Mock.Of<IGetBlogDetailUseCase>(),
            useCase.Object,
            Mock.Of<IEditBlogUseCase>(),
            Mock.Of<IDeleteBlogUseCase>(),
            Mock.Of<ILikeBlogUseCase>(),
            Mock.Of<IUnlikeBlogUseCase>(),
            Mock.Of<ICreateBlogCommentUseCase>());
        var action = await controller.GetComments(45, request, source.Token);

        var result = action.Should().BeOfType<OkObjectResult>().Subject;
        result.StatusCode.Should().Be(StatusCodes.Status200OK);
        var response = result.Value.Should().BeOfType<ApiResponse<Paged<CommentDto>>>().Subject;
        response.Success.Should().BeTrue();
        response.Data.Should().BeSameAs(expected);
        useCase.Verify(item => item.ExecuteAsync(45, request, source.Token), Times.Once);
    }

    // TEST-05: Delegate BLOG-06 to IEditBlogUseCase and return 200 OK wrapped in ApiResponse.
    [Fact]
    public async Task Edit_ShouldDelegateToUseCaseAndReturnOkResponse()
    {
        var expected = new BlogDetailDto { Id = 45, Title = "Chuyến đi cập nhật" };
        var request = new EditBlogRequestDto { Title = "Chuyến đi cập nhật" };
        var createUseCase = new Mock<ICreateBlogUseCase>();
        var editUseCase = new Mock<IEditBlogUseCase>();
        using var source = new CancellationTokenSource();
        editUseCase.Setup(item => item.ExecuteAsync(45, request, source.Token)).ReturnsAsync(expected);

        var action = await new BlogsController(
            Mock.Of<IGetPublicBlogsUseCase>(),
            createUseCase.Object,
            Mock.Of<IGetBlogDetailUseCase>(),
            Mock.Of<IGetBlogCommentsUseCase>(),
            editUseCase.Object,
            Mock.Of<IDeleteBlogUseCase>(),
            Mock.Of<ILikeBlogUseCase>(),
            Mock.Of<IUnlikeBlogUseCase>(),
            Mock.Of<ICreateBlogCommentUseCase>()).Edit(45, request, source.Token);

        var result = action.Should().BeOfType<OkObjectResult>().Subject;
        result.StatusCode.Should().Be(200);
        result.Value.Should().BeOfType<ApiResponse<BlogDetailDto>>().Which.Data.Should().BeSameAs(expected);
        editUseCase.Verify(item => item.ExecuteAsync(45, request, source.Token), Times.Once);
    }

    // TEST-06: Require the Traveler role and return a 200 ApiResponse with null data on soft deletion.
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

        var controller = new BlogsController(
            Mock.Of<IGetPublicBlogsUseCase>(),
            createUseCase.Object,
            Mock.Of<IGetBlogDetailUseCase>(),
            Mock.Of<IGetBlogCommentsUseCase>(),
            Mock.Of<IEditBlogUseCase>(),
            deleteUseCase.Object,
            Mock.Of<ILikeBlogUseCase>(),
            Mock.Of<IUnlikeBlogUseCase>(),
            Mock.Of<ICreateBlogCommentUseCase>());
        var action = await controller.Delete(45, source.Token);

        var result = action.Should().BeOfType<OkObjectResult>().Subject;
        result.StatusCode.Should().Be(200);
        var response = result.Value.Should().BeOfType<ApiResponse<object>>().Subject;
        response.Success.Should().BeTrue();
        response.Data.Should().BeNull();
        deleteUseCase.Verify(item => item.ExecuteAsync(45, source.Token), Times.Once);
    }

    // TEST-07: Verify Controller should require traveler role.
    /// <summary>Verify Controller should require traveler role.</summary>
    [Fact]
    public void Controller_ShouldRequireTravelerRole()
    {
        var authorize = typeof(BlogsController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .Single();
        authorize.Roles.Should().Be("TRAVELER");
    }

    // TEST-08: Verify Like should return ok with like response.
    /// <summary>Verify Like should return ok with like response.</summary>
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

    // TEST-09: Verify Unlike should return ok with like response.
    /// <summary>Verify Unlike should return ok with like response.</summary>
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

    // TEST-10: Verify Comment should return created with comment response.
    /// <summary>Verify Comment should return created with comment response.</summary>
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
