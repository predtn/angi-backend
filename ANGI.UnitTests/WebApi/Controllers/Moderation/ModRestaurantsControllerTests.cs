using ANGI.Application.Common.Interfaces.UseCases.Moderation;
using ANGI.Application.DTOs.Moderation;
using ANGI.WebApi.Common.Models;
using ANGI.WebApi.Controllers.Moderation;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace ANGI.UnitTests.WebApi.Controllers.Moderation;

/// <summary>Verifies thin controller delegation and moderator role restrictions.</summary>
public sealed class ModRestaurantsControllerTests
{
    // TEST-01: Delegate the route identifier, request and cancellation token and return the success envelope.
    /// <summary>Checks the controller preserves the use-case result and returns HTTP 200.</summary>
    [Fact]
    public async Task ReturnsUseCaseResponse()
    {
        using var cancellation = new CancellationTokenSource();
        var useCase = new Mock<IUpdateRestaurantModerationStatusUseCase>();
        var request = new UpdateRestaurantModerationStatusRequestDto();
        var expected = new UpdateRestaurantModerationStatusResponseDto();
        useCase.Setup(x => x.ExecuteAsync(7, request, cancellation.Token)).ReturnsAsync(expected);
        var result = (OkObjectResult)await new ModRestaurantsController(Mock.Of<IListModRestaurantsUseCase>(), Mock.Of<IGetModRestaurantUseCase>(), useCase.Object).Update(7, request, cancellation.Token);
        result.StatusCode.Should().Be(200);
        var envelope = (ApiResponse<UpdateRestaurantModerationStatusResponseDto>)result.Value!;
        envelope.Success.Should().BeTrue();
        envelope.Data.Should().BeSameAs(expected);
        useCase.Verify(x => x.ExecuteAsync(7, request, cancellation.Token), Times.Once);
    }

    // TEST-02: Require MOD or ADMIN at the HTTP controller boundary.
    /// <summary>Checks role authorization cannot accidentally expose moderation endpoints publicly.</summary>
    [Fact]
    public void RequiresModeratorRole()
    {
        var attribute = (AuthorizeAttribute)Attribute.GetCustomAttribute(typeof(ModRestaurantsController), typeof(AuthorizeAttribute))!;
        attribute.Roles.Should().Be("MOD,ADMIN");
    }
}
