using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Agent;
using ANGI.Application.Common.Interfaces.Repositories.Roadmap;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Agent;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.DTOs.Agent;
using ANGI.Application.Services.Agent;
using ANGI.Domain.Entities;
using FluentValidation;

namespace ANGI.Application.UseCases.Agent.CreateConversation;

/// <summary>Implements AGENT-01 without creating an empty official roadmap.</summary>
public sealed class CreateAgentConversationUseCase(IValidator<CreateAgentConversationRequestDto> validator,
    IAgentRepository repository, IRoadmapRepository roadmaps, ICurrentUserService user, IUnitOfWork unitOfWork,
    TimeProvider clock, AgentLimits limits) : ICreateAgentConversationUseCase
{
    /// <summary>Validates ownership, derives available trip values and persists expiring short-term state.</summary>
    public async Task<AgentConversationDto> ExecuteAsync(CreateAgentConversationRequestDto request, CancellationToken ct)
    {
        await validator.ValidateAndThrowAsync(request, ct);
        var userId = AgentConversationRules.UserId(user);
        var state = new AgentState();
        if (request.RoadmapId is { } id)
        {
            var roadmap = await roadmaps.GetOwnedDetailAsync(id, userId, ct)
                ?? throw new NotFoundException("ROADMAP_NOT_FOUND", "Không tìm thấy roadmap.");
            state = AgentContextBuilder.StateFromRoadmap(roadmap);
        }
        var conversation = new AgentConversation { UserId = userId, RoadmapId = request.RoadmapId,
            State = AgentJson.Write(state), ExpiresAt = clock.GetUtcNow().UtcDateTime.AddDays(limits.ActiveDays) };
        repository.AddConversation(conversation);
        await unitOfWork.SaveChangesAsync(ct);
        return new(conversation.Id, conversation.Version, "active", state, false, [], null);
    }
}
