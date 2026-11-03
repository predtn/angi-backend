using System.Diagnostics;
using System.Text.Json;
using ANGI.Application.Common.Interfaces.Services.Agent;
using Microsoft.Extensions.Logging;

namespace ANGI.Infrastructure.Services.Agent;

/// <summary>Emits structured, correlation-aware Agent metrics without raw messages or secrets.</summary>
public sealed class AgentTelemetry(ILogger<AgentTelemetry> logger) : IAgentTelemetry
{
    private readonly string _fallbackCorrelationId = Guid.NewGuid().ToString("N");

    /// <summary>Records safe diagnostic metadata, resource revisions and timing for QA and operations.</summary>
    public void Record(string name, long conversationId, long? turnId, long? attemptId, object? data = null) =>
        logger.LogInformation("Agent {Event} correlationId={CorrelationId} conversationId={ConversationId} turnId={TurnId} attemptId={AttemptId} data={Data}",
            name, Activity.Current?.TraceId.ToString() ?? _fallbackCorrelationId, conversationId, turnId, attemptId, data is null ? null : JsonSerializer.Serialize(data));
}
