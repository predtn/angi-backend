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
        // STEP-01: Create cancellation as CancellationTokenSource.
        using var cancellation = new CancellationTokenSource();
        // STEP-02: Mock IUpdateRestaurantModerationStatusUseCase.
        var useCase = new Mock<IUpdateRestaurantModerationStatusUseCase>();
        // STEP-03: Create request as UpdateRestaurantModerationStatusRequestDto and create expected as UpdateRestaurantModerationStatusResponseDto.
        var request = new UpdateRestaurantModerationStatusRequestDto();
        var expected = new UpdateRestaurantModerationStatusResponseDto();
        // STEP-04: Stub useCase.ExecuteAsync.
        useCase.Setup(x => x.ExecuteAsync(7, request, cancellation.Token)).ReturnsAsync(expected);
        // STEP-05: Derive result from Update().
        var result = (OkObjectResult)await new ModRestaurantsController(Mock.Of<IListModRestaurantsUseCase>(), Mock.Of<IGetModRestaurantUseCase>(), useCase.Object).Update(7, request, cancellation.Token);
        // STEP-06: Assert result.StatusCode is 200.
        result.StatusCode.Should().Be(200);
        // STEP-07: Derive envelope from result.Value.
        var envelope = (ApiResponse<UpdateRestaurantModerationStatusResponseDto>)result.Value!;
        // STEP-08: Assert envelope.Success is true and envelope.Data is the same instance as expected.
        envelope.Success.Should().BeTrue();
        envelope.Data.Should().BeSameAs(expected);
        // STEP-09: Verify ExecuteAsync is called once.
        useCase.Verify(x => x.ExecuteAsync(7, request, cancellation.Token), Times.Once);
    }

    // TEST-02: Require MOD or ADMIN at the HTTP controller boundary.
    /// <summary>Checks role authorization cannot accidentally expose moderation endpoints publicly.</summary>
    [Fact]
    public void RequiresModeratorRole()
    {
        // STEP-01: Derive attribute from Attribute.GetCustomAttribute().
        var attribute = (AuthorizeAttribute)Attribute.GetCustomAttribute(typeof(ModRestaurantsController), typeof(AuthorizeAttribute))!;
        // STEP-02: Assert attribute.Roles is "MOD,ADMIN".
        attribute.Roles.Should().Be("MOD,ADMIN");
    }
}
