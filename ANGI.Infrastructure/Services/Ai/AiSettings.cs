namespace ANGI.Infrastructure.Services.Ai;

/// <summary>AI provider settings for roadmap planning (project_architecture.md, Integrations and Configuration).</summary>
public sealed class AiSettings
{
    public const string SectionName = "Ai";

    /// <summary>Budget gateway base, ending with a slash; the transport also enforces gateway routing.</summary>
    public string BaseUrl { get; set; } = "http://127.0.0.1:18080/v1/";

    /// <summary>Scoped angi-chat gateway secret. Never put a real provider API key here.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Chat model with structured output (JSON schema) support.</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>Timeout of one call; the job runs in the background, so nobody waits on it.</summary>
    public int TimeoutSeconds { get; set; } = 30;
}
