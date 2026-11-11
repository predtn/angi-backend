using ANGI.Application.Common.Interfaces.UseCases.Agent;
using ANGI.Application.DTOs.Agent;
using ANGI.WebApi.Controllers.Agent;
using ANGI.WebApi.Common.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace ANGI.UnitTests.WebApi.Controllers.Agent;

/// <summary>Verifies thin Agent action forwarding and documented HTTP success statuses.</summary>
public sealed class AgentConversationsControllerTests
{
    // TEST-01: A completed message returns 200 while an in-flight duplicate returns 202 with the same API envelope.
    /// <summary>Checks the only dynamic success status at the Agent HTTP boundary.</summary>
    [Theory]
    [InlineData("completed", 200)][InlineData("processing", 202)]
    public async Task Send_UsesDocumentedTurnStatus(string status, int expected)
    {
        // STEP-01: Mock ISendAgentMessageUseCase.
        // STEP-02: Create request as SendAgentMessageRequestDto and create dto as AgentTurnDto.
        var useCase = new Mock<ISendAgentMessageUseCase>(); var request = new SendAgentMessageRequestDto();
        var dto = new AgentTurnDto(status, "reply", 2);
        // STEP-03: Stub useCase.ExecuteAsync.
        useCase.Setup(x => x.ExecuteAsync(7, request, It.IsAny<CancellationToken>())).ReturnsAsync(dto);
        // STEP-04: Create the AgentConversationsController under test as controller.
        var controller = new AgentConversationsController(Mock.Of<ICreateAgentConversationUseCase>(), Mock.Of<IGetAgentConversationUseCase>(), useCase.Object, Mock.Of<IConfirmAgentProposalUseCase>());
        // STEP-05: Run controller.Send with request and keep result.
        var result = (ObjectResult)await controller.Send(7, request, CancellationToken.None);
        // STEP-06: Assert result.StatusCode is expected and result.Value.Data is the same instance as dto.
        result.StatusCode.Should().Be(expected); ((ApiResponse<AgentTurnDto>)result.Value!).Data.Should().BeSameAs(dto);
    }

    // TEST-02: Agent resources require the Traveler role at the controller boundary.
    /// <summary>Verifies that anonymous and other-role callers are excluded before use-case execution.</summary>
    [Fact]
    public void Controller_RequiresTravelerRole() =>
        // STEP-01: Assert GetCustomAttributes().Cast().Single().Roles is "TRAVELER".
        typeof(AgentConversationsController).GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>().Single().Roles.Should().Be("TRAVELER");
}
