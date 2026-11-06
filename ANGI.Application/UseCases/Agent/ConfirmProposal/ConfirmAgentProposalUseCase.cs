using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Agent;
using ANGI.Application.Common.Interfaces.Services.Agent;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Agent;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.DTOs.Agent;
using ANGI.Application.Services.Agent.Proposals;
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
        // STEP-01: Validate the request.
        await validator.ValidateAndThrowAsync(request, ct);
        // STEP-02: Run AgentConversationRules.ValidateId.
        AgentConversationRules.ValidateId(id);
        // STEP-03: Prepare userId through AgentConversationRules.UserId.
        var userId = AgentConversationRules.UserId(user);
        try
        {
            // STEP-04: Prepare conversation, then call committer.PrepareAsync and keep prepared.
            var conversation = await repository.GetOwnedAsync(id, userId, ct) ?? throw AgentConversationRules.Missing();
            var prepared = await committer.PrepareAsync(conversation, userId, request, false, ct);
            // STEP-05: Return replay early when prepared.Replay exists.
            if (prepared.Replay is { } replay) return replay;
            // STEP-06: Open the database transaction.
            await using var transaction = await unitOfWork.BeginTransactionAsync(ct);
            // STEP-07: Set conversation.
            conversation = await repository.LockOwnedAsync(id, userId, ct) ?? throw AgentConversationRules.Missing();
            // STEP-08: Call committer.CommitLockedAsync and keep committed.
            var committed = await committer.CommitLockedAsync(conversation, prepared, userId, false, ct);
            // STEP-09: Return committed.Result early when committed.Applied is false.
            if (!committed.Applied) return committed.Result;
            // STEP-10: When conversation.ProcessingTurnId exists, prepare abandoned through repository.GetTurnAsync and check whether abandoned exists.
            if (conversation.ProcessingTurnId is { } abandonedTurn)
            {
                var abandoned = await repository.GetTurnAsync(id, abandonedTurn, ct);
                if (abandoned is not null) abandoned.ProcessingStatus = AgentProcessingStatus.Failed;
            }
            // STEP-11: Call AgentConversationRules.ClearLease.
            AgentConversationRules.ClearLease(conversation);
            // STEP-12: Increment conversation.Version.
            conversation.Version++;
            // STEP-13: Update conversation: ExpiresAt.
            conversation.ExpiresAt = clock.GetUtcNow().UtcDateTime.AddDays(limits.ActiveDays);
            // STEP-14: Save the pending changes.
            await unitOfWork.SaveChangesAsync(ct);
            // STEP-15: Commit the transaction.
            await transaction.CommitAsync(ct);
            // STEP-16: Call telemetry.Record.
            telemetry.Record("committed", id, null, null, new { request.ProposalVersion, committed.Result.RoadmapId, committed.Result.RoadmapUpdatedAt });
            // STEP-17: Return committed.Result.
            return committed.Result;
        }
        catch (Exception error)
        {
            // STEP-18: On Exception, call telemetry.Record and rethrow the caught exception.
            telemetry.Record("commit_failed", id, null, null, new { request.ProposalId, request.ProposalVersion,
                errorCode = (error as AppException)?.ErrorCode, errorType = error.GetType().Name,
                validationErrors = (error as AgentProposalValidationException)?.Validation.Errors });
            throw;
        }
    }
}
