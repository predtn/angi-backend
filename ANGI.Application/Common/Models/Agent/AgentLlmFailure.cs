namespace ANGI.Application.Common.Models.Agent;

/// <summary>Classifies provider failures without exposing raw prompts, credentials or provider response bodies.</summary>
public enum AgentLlmFailureKind
{
    Configuration,
    Timeout,
    TurnTimeout,
    Network,
    RateLimited,
    Authentication,
    RejectedRequest,
    ProviderUnavailable,
    InvalidResponse,
    InvalidSchema,
    Truncated,
    Refused
}

/// <summary>Carries safe diagnostics and an optional upstream retry delay independently of public API error codes.</summary>
public sealed record AgentLlmFailure(AgentLlmFailureKind Kind, int? HttpStatus = null, int? RetryAfterSeconds = null,
    string? ProviderCode = null, string? Reason = null);
