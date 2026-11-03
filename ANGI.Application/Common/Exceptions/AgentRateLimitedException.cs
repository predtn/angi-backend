namespace ANGI.Application.Common.Exceptions;

/// <summary>Rejects a new Agent turn after a persistent per-user allowance is exhausted.</summary>
public sealed class AgentRateLimitedException : AppException
{
    public int RetryAfterSeconds { get; }

    /// <summary>Supplies the retry delay without exposing user quota data to the LLM.</summary>
    public AgentRateLimitedException(int retryAfterSeconds) : base("AGENT_RATE_LIMITED", "Bạn đã vượt giới hạn tin nhắn Agent. Vui lòng thử lại sau.")
        => RetryAfterSeconds = retryAfterSeconds;
}
