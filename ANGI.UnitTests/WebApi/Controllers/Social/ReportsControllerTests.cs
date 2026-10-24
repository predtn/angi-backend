using ANGI.Application.Common.Interfaces.UseCases.Social;
using ANGI.Application.DTOs.Social;
using ANGI.WebApi.Common.Models;
using ANGI.WebApi.Controllers.Social;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace ANGI.UnitTests.WebApi.Controllers.Social;

/// <summary>Unit tests for the thin ReportsController HTTP boundary.</summary>
public sealed class ReportsControllerTests
{
    private readonly Mock<IReportBlogUseCase> _reportBlogUseCase = new();

    private ReportsController CreateController() => new(_reportBlogUseCase.Object);

    [Fact]
    public void Controller_ShouldRequireTravelerRole()
    {
        var authorize = typeof(ReportsController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .Single();
        authorize.Roles.Should().Be("TRAVELER");
    }

    [Fact]
    public async Task ReportBlog_ShouldReturnCreatedWithReportResponse()
    {
        const long blogId = 45;
        var request = new ReportBlogRequestDto { ReasonCode = "spam", Description = "Spam post" };
        var expected = new CreateReportResponseDto { ReportId = 88, Status = "pending" };
        using var source = new CancellationTokenSource();
        _reportBlogUseCase.Setup(item => item.ExecuteAsync(blogId, request, source.Token)).ReturnsAsync(expected);

        var action = await CreateController().ReportBlog(blogId, request, source.Token);

        var result = action.Should().BeOfType<ObjectResult>().Subject;
        result.StatusCode.Should().Be(201);
        result.Value.Should().BeOfType<ApiResponse<CreateReportResponseDto>>().Which.Data.Should().BeSameAs(expected);
        _reportBlogUseCase.Verify(item => item.ExecuteAsync(blogId, request, source.Token), Times.Once);
    }
}
