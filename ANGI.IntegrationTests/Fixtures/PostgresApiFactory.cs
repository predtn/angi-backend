using System.Collections.Concurrent;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.Services.Recommendation;
using ANGI.Application.Common.Models;
using ANGI.Application.Common.Models.Auth;
using ANGI.Application.Common.Models.Recommendation;
using ANGI.Infrastructure.Persistences;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace ANGI.IntegrationTests.Fixtures;

/// <summary>Hosts the real ANGI HTTP pipeline and persistence stack against a dedicated PostgreSQL test database.</summary>
public sealed class PostgresApiFactory : WebApplicationFactory<Program>
{
    public const string ConnectionStringVariable = "ANGI_TEST_POSTGRES_CONNECTION_STRING";
    public const string RunVariable = "RUN_POSTGRES_INTEGRATION_TESTS";
    public const string JwtSecret = "ANGI-end-to-end-test-secret-key-12345678901234567890";

    /// <summary>Gets the fake email boundary used to retrieve raw one-time tokens emitted by real use cases.</summary>
    public FakeEmailService Email { get; } = new();

    /// <summary>Gets the fake Google identity boundary used by AUTH-05 end-to-end tests.</summary>
    public FakeGoogleIdentityService Google { get; } = new();

    /// <summary>Gets the fake Cloudinary boundary used while media metadata is persisted by the real repository.</summary>
    public FakeCloudinaryService Cloudinary { get; } = new();

    /// <summary>Returns the explicitly configured dedicated test-database connection string.</summary>
    public string ConnectionString =>
        Environment.GetEnvironmentVariable(ConnectionStringVariable)
        ?? throw new InvalidOperationException($"{ConnectionStringVariable} is required.");

    /// <summary>
    /// Provides JWT and rate-limit values before Program reads them while registering services: overrides added in
    /// ConfigureAppConfiguration arrive too late for those reads, so the limits would stay at 100 requests a minute.
    /// </summary>
    protected override IHost CreateHost(IHostBuilder builder)
    {
        var values = new Dictionary<string, string>
        {
            ["Jwt__Issuer"] = "ANGI.IntegrationTests",
            ["Jwt__Audience"] = "ANGI.IntegrationTests.Client",
            ["Jwt__SecretKey"] = JwtSecret,
            ["RateLimit__GeneralPermitLimit"] = "10000",
            ["RateLimit__LoginPermitLimit"] = "10000",
            ["RateLimit__SensitiveAuthPermitLimit"] = "10000"
        };
        var previous = values.Keys.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        foreach (var (name, value) in values)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        try
        {
            return base.CreateHost(builder);
        }
        finally
        {
            foreach (var (name, value) in previous)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }

    /// <summary>Configures production services with the test database and deterministic external boundaries.</summary>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddConsole();
            logging.SetMinimumLevel(LogLevel.Error);
        });
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = ConnectionString,
                ["Jwt:Issuer"] = "ANGI.IntegrationTests",
                ["Jwt:Audience"] = "ANGI.IntegrationTests.Client",
                ["Jwt:SecretKey"] = JwtSecret,
                ["Jwt:AccessTokenMinutes"] = "15",
                ["Jwt:RefreshTokenDays"] = "30",
                ["RateLimit:GeneralPermitLimit"] = "10000",
                ["RateLimit:GeneralWindowSeconds"] = "60",
                ["RateLimit:LoginPermitLimit"] = "10000",
                ["RateLimit:LoginWindowMinutes"] = "15",
                ["RateLimit:SensitiveAuthPermitLimit"] = "10000",
                ["RateLimit:SensitiveAuthWindowSeconds"] = "60",
                ["Google:ClientId"] = "integration-client",
                ["Brevo:BaseUrl"] = "http://127.0.0.1:1/",
                ["Frontend:BaseUrl"] = "http://integration.test"
            });
        });
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IEmailService>();
            services.RemoveAll<IGoogleIdentityService>();
            services.RemoveAll<ICloudinaryService>();
            services.RemoveAll<IRecommendationService>();
            services.AddSingleton(Email);
            services.AddSingleton<IEmailService>(Email);
            services.AddSingleton(Google);
            services.AddSingleton<IGoogleIdentityService>(Google);
            services.AddSingleton(Cloudinary);
            services.AddSingleton<ICloudinaryService>(Cloudinary);
            services.AddSingleton<IRecommendationService, FakeRecommendationService>();
        });
    }

    /// <summary>Drops and recreates the application schema, applies migrations, and clears fake-service state.</summary>
    public async Task ResetDatabaseAsync()
    {
        EnsureDedicatedTestDatabase(ConnectionString);
        using var scope = Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ANGIContext>();
        await context.Database.ExecuteSqlRawAsync("DROP SCHEMA IF EXISTS core CASCADE");
        await context.Database.MigrateAsync();
        if (scope.ServiceProvider.GetService<IMemoryCache>() is MemoryCache cache)
        {
            cache.Compact(1.0);
        }

        Email.Reset();
        Google.Reset();
        Cloudinary.Reset();
    }

    /// <summary>Executes an assertion or seed operation with a fresh scoped database context.</summary>
    public async Task ExecuteDbAsync(Func<ANGIContext, Task> operation)
    {
        using var scope = Services.CreateScope();
        await operation(scope.ServiceProvider.GetRequiredService<ANGIContext>());
    }

    /// <summary>Executes a query with a fresh scoped database context and returns its result.</summary>
    public async Task<T> ExecuteDbAsync<T>(Func<ANGIContext, Task<T>> operation)
    {
        using var scope = Services.CreateScope();
        return await operation(scope.ServiceProvider.GetRequiredService<ANGIContext>());
    }

    /// <summary>Rejects missing or non-test database names before any destructive schema reset.</summary>
    public static void EnsureDedicatedTestDatabase(string connectionString)
    {
        var database = new NpgsqlConnectionStringBuilder(connectionString).Database;
        if (string.IsNullOrWhiteSpace(database) ||
            !database.Contains("test", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The PostgreSQL integration database name must contain 'test'.");
        }
    }
}

/// <summary>Skips PostgreSQL end-to-end tests unless a dedicated test database was explicitly enabled.</summary>
public sealed class PostgresEndToEndFactAttribute : FactAttribute
{
    /// <summary>Validates the opt-in flag and the dedicated test-database name during test discovery.</summary>
    public PostgresEndToEndFactAttribute()
    {
        var connectionString = Environment.GetEnvironmentVariable(PostgresApiFactory.ConnectionStringVariable);
        if (Environment.GetEnvironmentVariable(PostgresApiFactory.RunVariable) != "1" ||
            string.IsNullOrWhiteSpace(connectionString))
        {
            Skip = $"Set {PostgresApiFactory.RunVariable}=1 and {PostgresApiFactory.ConnectionStringVariable}.";
            return;
        }

        try
        {
            PostgresApiFactory.EnsureDedicatedTestDatabase(connectionString);
        }
        catch (InvalidOperationException exception)
        {
            Skip = exception.Message;
        }
    }
}

/// <summary>Captures raw tokens sent by authentication use cases without contacting Brevo.</summary>
public sealed class FakeEmailService : IEmailService
{
    private readonly ConcurrentDictionary<string, string> _verificationTokens = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _resetTokens = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Records the latest verification token for the recipient.</summary>
    public Task SendEmailVerificationAsync(
        string toEmail,
        string displayName,
        string token,
        TimeSpan validFor,
        CancellationToken ct)
    {
        _verificationTokens[toEmail] = token;
        return Task.CompletedTask;
    }

    /// <summary>Records the latest password-reset token for the recipient.</summary>
    public Task SendPasswordResetAsync(
        string toEmail,
        string displayName,
        string token,
        TimeSpan validFor,
        CancellationToken ct)
    {
        _resetTokens[toEmail] = token;
        return Task.CompletedTask;
    }

    /// <summary>Returns the verification token emitted for the supplied email.</summary>
    public string VerificationToken(string email) => _verificationTokens[email];

    /// <summary>Returns the password-reset token emitted for the supplied email.</summary>
    public string ResetToken(string email) => _resetTokens[email];

    /// <summary>Clears captured tokens between isolated end-to-end scenarios.</summary>
    public void Reset()
    {
        _verificationTokens.Clear();
        _resetTokens.Clear();
    }
}

/// <summary>Maps deterministic test ID tokens to trusted Google identities.</summary>
public sealed class FakeGoogleIdentityService : IGoogleIdentityService
{
    private readonly ConcurrentDictionary<string, GoogleIdentity> _identities = new(StringComparer.Ordinal);

    /// <summary>Registers the trusted identity returned for one test ID token.</summary>
    public void Add(string idToken, GoogleIdentity identity) => _identities[idToken] = identity;

    /// <summary>Returns the registered trusted identity without making a Google network request.</summary>
    public Task<GoogleIdentity> VerifyIdTokenAsync(string idToken, CancellationToken ct) =>
        Task.FromResult(_identities[idToken]);

    /// <summary>Clears registered identities between isolated end-to-end scenarios.</summary>
    public void Reset() => _identities.Clear();
}

/// <summary>Returns deterministic storage metadata while the real media use case persists it.</summary>
public sealed class FakeCloudinaryService : ICloudinaryService
{
    private int _sequence;

    /// <summary>Creates a provider-shaped storage key and URL without external network access.</summary>
    public Task<MediaUploadResult> UploadAsync(
        Stream file,
        string fileName,
        string mimeType,
        string folder,
        bool isPrivate,
        CancellationToken cancellationToken)
    {
        var sequence = Interlocked.Increment(ref _sequence);
        var resource = mimeType == "application/pdf" ? "raw" : "image";
        var type = isPrivate ? "authenticated" : "upload";
        var storageKey = $"{resource}/{type}/{folder}/integration-{sequence}";
        return Task.FromResult(new MediaUploadResult(storageKey, $"https://media.integration/{sequence}"));
    }

    /// <summary>Returns a deterministic delivery URL for persisted storage keys.</summary>
    public string GetUrl(string storageKey) => $"https://media.integration/signed/{Uri.EscapeDataString(storageKey)}";

    /// <summary>Resets generated storage identifiers between isolated scenarios.</summary>
    public void Reset() => _sequence = 0;
}

/// <summary>Provides deterministic recommendation responses required by authentication use cases.</summary>
internal sealed class FakeRecommendationService : IRecommendationService
{
    /// <summary>Returns a deterministic stored-survey result.</summary>
    public Task<SurveySubmissionResult> SubmitSurveyAsync(
        int userId,
        IReadOnlyList<SurveyAnswer> answers,
        DateTime? occurredAt,
        CancellationToken ct) =>
        Task.FromResult(new SurveySubmissionResult(answers.Count, true, occurredAt ?? DateTime.UtcNow));

    /// <summary>Returns candidates in their supplied order.</summary>
    public Task<RecommendationResult> RecommendAsync(
        int? userId,
        IReadOnlyList<int> candidateDishIds,
        int? topK,
        RecommendationContext context,
        CancellationToken ct) =>
        Task.FromResult(new RecommendationResult(null, candidateDishIds.Take(topK ?? 50).ToArray(), []));

    /// <summary>Accepts impression recording without contacting the recommendation service.</summary>
    public Task RecordImpressionsAsync(
        int userId,
        Guid requestId,
        IReadOnlyList<int> dishIds,
        DateTime? shownAt,
        CancellationToken ct) => Task.CompletedTask;

    /// <summary>Returns an incomplete survey state for deterministic authentication responses.</summary>
    public Task<SurveyStatus?> GetSurveyStatusAsync(int userId, CancellationToken ct) =>
        Task.FromResult<SurveyStatus?>(new SurveyStatus(false, null, 0));
}
