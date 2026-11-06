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
        // STEP-01: Run AgentConversationRules.ValidateId.
        AgentConversationRules.ValidateId(id);
        // STEP-02: Prepare conversation from AgentConversationRules.Missing and now from clock.GetUtcNow.
        var conversation = await repository.GetOwnedAsync(id, AgentConversationRules.UserId(user), ct) ?? throw AgentConversationRules.Missing();
        var now = clock.GetUtcNow().UtcDateTime;
        // STEP-03: Check the active rule through AgentConversationRules.RequireActive.
        AgentConversationRules.RequireActive(conversation, now);
        // STEP-04: Prepare proposal, then load history through repository.GetCompletedHistoryAsync.
        var proposal = conversation.CurrentProposalId is { } proposalId ? await repository.GetProposalSnapshotAsync(id, proposalId, ct) : null;
        var history = await repository.GetCompletedHistoryAsync(id, limits.MaxTranscriptMessages, ct);
        // STEP-05: Return the new result.
        return new(id, conversation.Version, AgentJson.Name(conversation.Status), AgentJson.Read<AgentState>(conversation.State),
            AgentConversationRules.Busy(conversation, now), history.Where(x => x.Role != AgentMessageRole.Tool &&
                x.SequenceNo >= (AgentJson.Read<AgentState>(conversation.State).HistoryStartSequence ?? 0))
                .Select(x => new AgentTranscript(AgentJson.Name(x.Role), AgentJson.Read<AgentStoredMessage>(x.Payload).Content)).ToArray(),
            proposal is null ? null : AgentContextBuilder.MapProposal(proposal));
    }
}
