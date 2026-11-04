using ANGI.Application.Common.Interfaces.UseCases.Administration;
using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Administration;
using ANGI.WebApi.Common.Models;
using ANGI.WebApi.Controllers.Administration;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace ANGI.UnitTests.WebApi.Controllers.Administration;

public sealed class AdminOutboxControllerTests
{
    [Fact]
    public async Task GetFailedOutboxMessages_ShouldReturnOkApiResponse()
    {
        var request = new GetOutboxMessagesRequestDto();
        var expected = new Paged<OutboxMessageDto>();
        var useCase = new Mock<IGetFailedOutboxMessagesUseCase>();
        useCase.Setup(item => item.ExecuteAsync(request, It.IsAny<CancellationToken>())).ReturnsAsync(expected);
        var controller = new AdminOutboxController(useCase.Object);

        var action = await controller.GetFailedOutboxMessages(request, CancellationToken.None);

        var result = action.Should().BeOfType<OkObjectResult>().Subject;
        result.Value.Should().BeOfType<ApiResponse<Paged<OutboxMessageDto>>>().Which.Data.Should().BeSameAs(expected);
    }
}
