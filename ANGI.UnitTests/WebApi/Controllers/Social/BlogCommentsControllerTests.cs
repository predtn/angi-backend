using ANGI.Application.Common.Interfaces.UseCases.Social;
using ANGI.Application.DTOs.Social;
using ANGI.WebApi.Common.Models;
using ANGI.WebApi.Controllers.Social;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace ANGI.UnitTests.WebApi.Controllers.Social;

/// <summary>Unit tests for the BLOG-10 and BLOG-11 HTTP boundary.</summary>
public sealed class BlogCommentsControllerTests
{
    // TEST-01: Verifies traveler role on controller level.
    /// <summary>Ensures only travelers can manage blog comments.</summary>
    [Fact]
    public void Controller_ShouldRequireTravelerRole()
    {
        var authorize = typeof(BlogCommentsController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .Single();
        authorize.Roles.Should().Be("TRAVELER");
    }

    // TEST-02: Receives BLOG-11, delegates to use case, and returns 200 OK with null Data.
    /// <summary>Ensures a deleted comment returns 200 with null data and forwards the cancellation token.</summary>
    [Fact]
    public async Task DeleteComment_ShouldReturnOkResponseWithNullData()
    {
        const long blogId = 45;
        const long commentId = 302;

        var deleteCommentUseCase = new Mock<IDeleteBlogCommentUseCase>();
        using var source = new CancellationTokenSource();
        deleteCommentUseCase.Setup(u => u.ExecuteAsync(blogId, commentId, source.Token))
            .Returns(Task.CompletedTask);

        var controller = new BlogCommentsController(deleteCommentUseCase.Object);

        var action = await controller.DeleteComment(blogId, commentId, source.Token);

        var result = action.Should().BeOfType<OkObjectResult>().Subject;
        result.StatusCode.Should().Be(200);
        var envelope = result.Value.Should().BeOfType<ApiResponse<object?>>().Subject;
        envelope.Success.Should().BeTrue();
        envelope.Data.Should().BeNull();
        deleteCommentUseCase.Verify(u => u.ExecuteAsync(blogId, commentId, source.Token), Times.Once);
    }
}
