using ANGI.Application.Common.Interfaces.Repositories.Agent;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Agent;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.DTOs.Agent;
using ANGI.Application.Services.Agent;
using ANGI.Domain.Enums;

namespace ANGI.Application.UseCases.Agent.GetConversation;

/// <summary>Implements AGENT-03 as a read that never renews the idle expiry.</summary>
public sealed class GetAgentConversationUseCase(IAgentRepository repository, ICurrentUserService user,
    TimeProvider clock, AgentLimits limits) : IGetAgentConversationUseCase
{
    /// <summary>Restores the owner's state and completed history without exposing partial attempt output.</summary>
    public async Task<AgentConversationDto> ExecuteAsync(long id, CancellationToken ct)
    {
        AgentConversationRules.ValidateId(id);
        var conversation = await repository.GetOwnedAsync(id, AgentConversationRules.UserId(user), ct) ?? throw AgentConversationRules.Missing();
        var now = clock.GetUtcNow().UtcDateTime;
        AgentConversationRules.RequireActive(conversation, now);
        var proposal = conversation.CurrentProposalId is { } proposalId ? await repository.GetProposalSnapshotAsync(id, proposalId, ct) : null;
        var history = await repository.GetCompletedHistoryAsync(id, limits.MaxHistoryMessages, ct);
        return new(id, conversation.Version, AgentJson.Name(conversation.Status), AgentJson.Read<AgentState>(conversation.State),
            AgentConversationRules.Busy(conversation, now), history.Where(x => x.Role != AgentMessageRole.Tool)
                .Select(x => new AgentTranscript(AgentJson.Name(x.Role), AgentJson.Read<AgentStoredMessage>(x.Payload).Content)).ToArray(),
            proposal is null ? null : AgentContextBuilder.MapProposal(proposal));
    }
}
