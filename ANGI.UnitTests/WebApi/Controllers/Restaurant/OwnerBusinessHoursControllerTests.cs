using ANGI.Application.Common.Interfaces.UseCases.Restaurant;
using ANGI.Application.DTOs.Restaurant;
using ANGI.WebApi.Common.Models;
using ANGI.WebApi.Controllers.Restaurant;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace ANGI.UnitTests.WebApi.Controllers.Restaurant;

public sealed class OwnerBusinessHoursControllerTests
{
    // TEST-01: Allow only restaurant owners, as column "Auth / Role" of OWN-04 says.
    [Fact]
    public void Controller_ShouldAllowOnlyRestaurantOwners()
    {
        var attribute = typeof(OwnerBusinessHoursController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .Should().ContainSingle().Subject;

        attribute.Roles.Should().Be("RESTAURANT_OWNER");
    }

    // TEST-02: Wrap the OWN-04 result in a 200 ApiResponse.
    [Fact]
    public async Task Update_ShouldReturnOkWithUseCaseResult()
    {
        var request = new UpdateBusinessHoursRequestDto
        {
            Hours = [new BusinessHourDto { DayOfWeek = 1, OpenTime = "06:00", CloseTime = "13:30" }]
        };
        var expected = new UpdateBusinessHoursResponseDto { Hours = request.Hours, IsOpenNow = true };
        var useCase = new Mock<IUpdateBusinessHoursUseCase>();
        useCase.Setup(x => x.ExecuteAsync(request, It.IsAny<CancellationToken>())).ReturnsAsync(expected);

        var action = await new OwnerBusinessHoursController(useCase.Object).Update(request, CancellationToken.None);

        var result = action.Should().BeOfType<OkObjectResult>().Subject;
        result.StatusCode.Should().Be(StatusCodes.Status200OK);
        var response = result.Value.Should().BeOfType<ApiResponse<UpdateBusinessHoursResponseDto>>().Subject;
        response.Success.Should().BeTrue();
        response.Data.Should().BeSameAs(expected);
    }
}
