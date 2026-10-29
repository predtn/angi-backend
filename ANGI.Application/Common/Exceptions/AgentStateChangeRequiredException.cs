using ANGI.Application.Common.Models.Agent;

namespace ANGI.Application.Common.Exceptions;

/// <summary>Carries validated constraints for backend-controlled chat consent instead of authorizing a model change.</summary>
public sealed class AgentStateChangeRequiredException(AgentState proposed)
    : ConflictException("PROPOSAL_NOT_COMMITTABLE", "Cần xác nhận thay đổi ràng buộc chuyến đi.")
{
    public AgentState Proposed { get; } = proposed;
}
