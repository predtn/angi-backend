namespace ANGI.IntegrationTests.Fixtures;

/// <summary>Requires a dedicated PostgreSQL database and an explicitly enabled real reco HTTP service.</summary>
public sealed class RecoEndToEndTheoryAttribute : TheoryAttribute
{
    public RecoEndToEndTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable(PostgresApiFactory.RunVariable) != "1" ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PostgresApiFactory.ConnectionStringVariable)) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANGI_TEST_RECO_URL")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANGI_TEST_RECO_API_KEY")))
            Skip = "Enable PostgreSQL tests and set ANGI_TEST_RECO_URL / ANGI_TEST_RECO_API_KEY for real service tests.";
        else PostgresApiFactory.EnsureDedicatedTestDatabase(Environment.GetEnvironmentVariable(PostgresApiFactory.ConnectionStringVariable)!);
    }
}
