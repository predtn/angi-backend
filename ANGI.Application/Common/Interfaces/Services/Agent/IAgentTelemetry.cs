namespace ANGI.Application.Common.Interfaces.Services.Agent;

/// <summary>Records safe structured Agent diagnostics without raw prompts or credentials.</summary>
public interface IAgentTelemetry
{
    /// <summary>Records a correlation-safe event with resource IDs, timing and validation metadata.</summary>
    void Record(string name, long conversationId, long? turnId, long? attemptId, object? data = null);

    /// <summary>Adds content (user text, raw model output, tool results, reply) to the local Development trace only.</summary>
    /// <remarks>Never goes to the normal log. Does nothing unless the Development trace is switched on.</remarks>
    void Trace(string name, long conversationId, long? turnId, long? attemptId, object? data = null) { }
}
