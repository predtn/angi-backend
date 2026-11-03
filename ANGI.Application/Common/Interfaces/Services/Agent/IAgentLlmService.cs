using ANGI.Application.Common.Models.Agent;

namespace ANGI.Application.Common.Interfaces.Services.Agent;

/// <summary>Provider-independent structured LLM adapter with no database or ownership authority.</summary>
public interface IAgentLlmService
{
    /// <summary>Produces one bounded tool round, output or classified failure; only invalid schema permits one repair.</summary>
    Task<AgentLlmStep?> RunAsync(AgentContext context, bool repair, CancellationToken ct);
}
