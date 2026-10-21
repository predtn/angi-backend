using ANGI.Application.Common.Interfaces.UseCases.Social;
using ANGI.Application.DTOs.Social;
using ANGI.WebApi.Common.Models;
using ANGI.WebApi.Controllers.Social;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace ANGI.UnitTests.WebApi.Controllers.Social;

public sealed class BlogsControllerTests
{
    private readonly Mock<ICreateBlogUseCase> _createBlogUseCase = new();

    private BlogsController CreateController() => new(_createBlogUseCase.Object);

    [Fact]
    public void Controller_ShouldRequireTravelerRole()
    {
        var authorizeAttribute = typeof(BlogsController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .FirstOrDefault();

        authorizeAttribute.Should().NotBeNull();
        authorizeAttribute!.Roles.Should().Be("TRAVELER");
    }

    [Fact]
    public async Task Create_ShouldCallUseCaseAndReturnCreated()
    {
        // Arrange
        var request = new CreateBlogRequestDto
        {
            Title = "Bài viết mới",
            Content = "<p>Nội dung</p>"
        };

        var blogDetail = new BlogDetailDto
        {
            Id = 1,
            Title = "Bài viết mới",
            Slug = "bai-viet-moi",
            Content = "<p>Nội dung</p>"
        };

        _createBlogUseCase.Setup(u => u.ExecuteAsync(request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(blogDetail);

        var controller = CreateController();

        // Act
        var result = await controller.Create(request, CancellationToken.None);

        // Assert
        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(StatusCodes.Status201Created);

        var response = objectResult.Value.Should().BeOfType<ApiResponse<BlogDetailDto>>().Subject;
        response.Success.Should().BeTrue();
        response.Data.Should().BeEquivalentTo(blogDetail);

        _createBlogUseCase.Verify(u => u.ExecuteAsync(request, It.IsAny<CancellationToken>()), Times.Once);
    }
}
