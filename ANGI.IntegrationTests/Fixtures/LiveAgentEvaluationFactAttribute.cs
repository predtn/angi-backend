namespace ANGI.IntegrationTests.Fixtures;

/// <summary>Requires an explicit opt-in and a key, so the paid Agent evaluation never runs in an ordinary test run.</summary>
public sealed class LiveAgentEvaluationFactAttribute : FactAttribute
{
    public const string RunVariable = "RUN_LIVE_AGENT_EVALUATION";
    public const string ApiKeyVariable = "ANGI_TEST_AI_API_KEY";

    /// <summary>Skips unless the run variable is 1 and an API key is set.</summary>
    public LiveAgentEvaluationFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(RunVariable) != "1" || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ApiKeyVariable)))
            Skip = $"Paid provider calls: set {RunVariable}=1 and {ApiKeyVariable} only after the user approved the estimated cost.";
    }
}
