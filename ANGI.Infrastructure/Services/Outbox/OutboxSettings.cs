namespace ANGI.Infrastructure.Services.Outbox;

/// <summary>Outbox worker settings (project_architecture.md, Outbox and Configuration).</summary>
public sealed class OutboxSettings
{
    public const string SectionName = "Outbox";

    /// <summary>Wait between polls when the last batch was not full.</summary>
    public int PollSeconds { get; set; } = 2;

    /// <summary>Messages claimed per poll.</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>A message still failing after this many attempts becomes dead.</summary>
    public int MaxAttempts { get; set; } = 10;

    /// <summary>Timeout of one call to reco. Longer than the 800 ms of direct calls: nobody waits on it.</summary>
    public int TimeoutSeconds { get; set; } = 10;
}
