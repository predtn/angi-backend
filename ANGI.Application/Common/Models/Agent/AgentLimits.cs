namespace ANGI.Application.Common.Models.Agent;

/// <summary>Backend-enforced resource and retention limits for the Agent MVP.</summary>
public sealed class AgentLimits
{
    public const string SectionName = "Agent";
    public int MaxToolRounds { get; set; } = 3;
    public int MaxToolCalls { get; set; } = 6;
    public int MaxLlmCalls { get; set; } = 8;
    public int MaxMessageLength { get; set; } = 4000;
    public int MaxContextCharacters { get; set; } = 120000;
    public int MaxHistoryMessages { get; set; } = 20;
    public int MaxCandidates { get; set; } = 2000;
    public int MaxRanked { get; set; } = 200;
    public int LlmTimeoutSeconds { get; set; } = 60;
    public int TimeoutSeconds { get; set; } = 90;
    public int LeaseSeconds { get; set; } = 120;
    public int MessagesPerMinute { get; set; } = 10;
    public int MessagesPerDay { get; set; } = 100;
    public int ActiveDays { get; set; } = 7;
    public int RetentionDays { get; set; } = 30;
    public int CleanupSeconds { get; set; } = 3600;
    /// <summary>Per-call completion budget, including provider reasoning tokens and structured output.</summary>
    public int MaxOutputTokens { get; set; } = 16000;
    /// <summary>Optional provider-supported reasoning effort; null preserves the provider default.</summary>
    public string? ReasoningEffort { get; set; }
}
