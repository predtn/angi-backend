using ANGI.Application.Common.Interfaces.UseCases.Agent;
using ANGI.Application.DTOs.Agent;
using ANGI.WebApi.Common.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ANGI.WebApi.Controllers.Agent;

/// <summary>Exposes owned short-term food-trip conversations to authenticated travelers.</summary>
[ApiController]
[Route("api/v1/agent/conversations")]
[Authorize(Roles = "TRAVELER")]
public sealed class AgentConversationsController(ICreateAgentConversationUseCase create, IGetAgentConversationUseCase get,
    ISendAgentMessageUseCase send, IConfirmAgentProposalUseCase confirm) : ControllerBase
{
    /// <summary>AGENT-01 opens empty state or an owned roadmap context without changing the official itinerary.</summary>
    [HttpPost]
    public async Task<IActionResult> Create(CreateAgentConversationRequestDto request, CancellationToken ct)
    {
        var result = await create.ExecuteAsync(request, ct);
        return StatusCode(result.Reused ? StatusCodes.Status200OK : StatusCodes.Status201Created,
            new ApiResponse<AgentConversationDto> { Success = true, Data = result });
    }

    /// <summary>AGENT-03 restores bounded completed history and the current preview without extending expiry.</summary>
    [HttpGet("{id:long}")]
    public async Task<IActionResult> Get(long id, CancellationToken ct) =>
        Ok(new ApiResponse<AgentConversationDto> { Success = true, Data = await get.ExecuteAsync(id, ct) });

    /// <summary>AGENT-02 processes an idempotent turn, or returns 202 while the same logical message is processing.</summary>
    [HttpPost("{id:long}/messages")]
    public async Task<IActionResult> Send(long id, SendAgentMessageRequestDto request, CancellationToken ct)
    {
        var result = await send.ExecuteAsync(id, request, ct);
        return StatusCode(result.Status == "processing" ? StatusCodes.Status202Accepted : StatusCodes.Status200OK,
            new ApiResponse<AgentTurnDto> { Success = true, Data = result });
    }

    /// <summary>AGENT-04 confirms exactly one displayed proposal version without calling the LLM.</summary>
    [HttpPost("{id:long}/confirm")]
    public async Task<IActionResult> Confirm(long id, ConfirmAgentProposalRequestDto request, CancellationToken ct) =>
        Ok(new ApiResponse<AgentCommitDto> { Success = true, Data = await confirm.ExecuteAsync(id, request, ct) });
}
