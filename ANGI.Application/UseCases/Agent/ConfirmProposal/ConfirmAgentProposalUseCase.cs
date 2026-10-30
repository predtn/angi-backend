using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Agent;
using ANGI.Application.Common.Interfaces.Services.Agent;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Agent;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.DTOs.Agent;
using ANGI.Application.Services.Agent;
using ANGI.Domain.Enums;
using FluentValidation;

namespace ANGI.Application.UseCases.Agent.ConfirmProposal;

/// <summary>Keeps AGENT-04 compatible while sharing its deterministic writer with chat-only saves.</summary>
public sealed class ConfirmAgentProposalUseCase(IValidator<ConfirmAgentProposalRequestDto> validator,
    IAgentRepository repository, ICurrentUserService user, IUnitOfWork unitOfWork, AgentProposalCommitter committer,
    TimeProvider clock, AgentLimits limits, IAgentTelemetry telemetry) : IConfirmAgentProposalUseCase
{
    /// <summary>Validates the requested revision, runs route preflight and applies one transactional business effect.</summary>
    public async Task<AgentCommitDto> ExecuteAsync(long id, ConfirmAgentProposalRequestDto request, CancellationToken ct)
    {
        await validator.ValidateAndThrowAsync(request, ct);
        AgentConversationRules.ValidateId(id);
        var userId = AgentConversationRules.UserId(user);
        try
        {
            var conversation = await repository.GetOwnedAsync(id, userId, ct) ?? throw AgentConversationRules.Missing();
            var prepared = await committer.PrepareAsync(conversation, userId, request, false, ct);
            if (prepared.Replay is { } replay) return replay;
            await using var transaction = await unitOfWork.BeginTransactionAsync(ct);
            conversation = await repository.LockOwnedAsync(id, userId, ct) ?? throw AgentConversationRules.Missing();
            var committed = await committer.CommitLockedAsync(conversation, prepared, userId, false, ct);
            if (!committed.Applied) return committed.Result;
            if (conversation.ProcessingTurnId is { } abandonedTurn)
            {
                var abandoned = await repository.GetTurnAsync(id, abandonedTurn, ct);
                if (abandoned is not null) abandoned.ProcessingStatus = AgentProcessingStatus.Failed;
            }
            AgentConversationRules.ClearLease(conversation);
            conversation.Version++;
            conversation.ExpiresAt = clock.GetUtcNow().UtcDateTime.AddDays(limits.ActiveDays);
            await unitOfWork.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            telemetry.Record("committed", id, null, null, new { request.ProposalVersion, committed.Result.RoadmapId, committed.Result.RoadmapUpdatedAt });
            return committed.Result;
        }
        catch (Exception error)
        {
            telemetry.Record("commit_failed", id, null, null, new { request.ProposalId, request.ProposalVersion,
                errorCode = (error as AppException)?.ErrorCode, errorType = error.GetType().Name,
                validationErrors = (error as AgentProposalValidationException)?.Validation.Errors });
            throw;
        }
    }
}
