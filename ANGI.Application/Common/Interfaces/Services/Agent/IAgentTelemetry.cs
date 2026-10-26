namespace ANGI.Application.Common.Interfaces.Services.Agent;

/// <summary>Records safe structured Agent diagnostics without raw prompts or credentials.</summary>
public interface IAgentTelemetry
{
    /// <summary>Records a correlation-safe event with resource IDs, timing and validation metadata.</summary>
    void Record(string name, long conversationId, long? turnId, long? attemptId, object? data = null);
}
