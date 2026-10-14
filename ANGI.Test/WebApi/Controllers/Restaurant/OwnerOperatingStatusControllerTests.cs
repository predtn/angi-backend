using ANGI.Application.Common.Interfaces.UseCases.Restaurant;
using ANGI.Application.DTOs.Restaurant;
using ANGI.WebApi.Common.Models;
using ANGI.WebApi.Controllers.Restaurant;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace ANGI.Test.WebApi.Controllers.Restaurant;

public sealed class OwnerOperatingStatusControllerTests
{
    // TEST-01: Allow only restaurant owners, as column "Auth / Role" of OWN-05 says.
    [Fact]
    public void Controller_ShouldAllowOnlyRestaurantOwners()
    {
        typeof(OwnerOperatingStatusController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .Should().ContainSingle()
            .Which.Roles.Should().Be("RESTAURANT_OWNER");
    }

    // TEST-02: Wrap the OWN-05 result in a 200 ApiResponse.
    [Fact]
    public async Task Update_ShouldReturnOkWithUseCaseResult()
    {
        var request = new UpdateOperatingStatusRequestDto { OperatingStatus = "temporarily_closed", Note = "Nghỉ Tết" };
        var expected = new UpdateOperatingStatusResponseDto { OperatingStatus = "temporarily_closed" };
        var useCase = new Mock<IUpdateOperatingStatusUseCase>();
        useCase.Setup(x => x.ExecuteAsync(request, It.IsAny<CancellationToken>())).ReturnsAsync(expected);

        var action = await new OwnerOperatingStatusController(useCase.Object).Update(request, CancellationToken.None);

        var result = action.Should().BeOfType<OkObjectResult>().Subject;
        result.StatusCode.Should().Be(StatusCodes.Status200OK);
        var response = result.Value.Should().BeOfType<ApiResponse<UpdateOperatingStatusResponseDto>>().Subject;
        response.Success.Should().BeTrue();
        response.Data.Should().BeSameAs(expected);
    }
}
