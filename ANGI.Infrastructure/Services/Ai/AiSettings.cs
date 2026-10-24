namespace ANGI.Infrastructure.Services.Ai;

/// <summary>AI provider settings for roadmap planning (project_architecture.md, Integrations and Configuration).</summary>
public sealed class AiSettings
{
    public const string SectionName = "Ai";

    /// <summary>OpenAI API base, ending with a slash.</summary>
    public string BaseUrl { get; set; } = "https://api.openai.com/v1/";

    /// <summary>Secret; empty in appsettings.json. Without it the job schedules days itself.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Chat model with structured output (JSON schema) support.</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>Timeout of one call; the job runs in the background, so nobody waits on it.</summary>
    public int TimeoutSeconds { get; set; } = 30;
}
