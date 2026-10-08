using ANGI.Application.Common.Interfaces.Services;
using ANGI.Infrastructure.Services.Email;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ANGI.Test.Infrastructure.Services.Email
{
    public sealed class BrevoLiveTests
    {
        // TEST-01: Send both account emails through the real Brevo account to BREVO_LIVE_TEST_TO. Area: Brevo live integration.
        [BrevoLiveFact]
        public async Task SendsVerificationAndPasswordResetEmailsThroughRealBrevo()
        {
            var appsettingsPath = Path.GetFullPath(
                Path.Combine(AppContext.BaseDirectory, "../../../../ANGI.WebApi/appsettings.json"));
            var configuration = new ConfigurationBuilder()
                .AddJsonFile(appsettingsPath)
                .AddUserSecrets(typeof(ANGI.WebApi.DependencyInjection).Assembly)
                .AddEnvironmentVariables()
                .Build();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddEmailInfrastructure(configuration);
            using var provider = services.BuildServiceProvider();
            var emailService = provider.GetRequiredService<IEmailService>();
            var recipient = Environment.GetEnvironmentVariable("BREVO_LIVE_TEST_TO")!;

            await emailService.SendEmailVerificationAsync(
                recipient, "ANGI Live Test", "live-test-verify-token", TimeSpan.FromHours(24), CancellationToken.None);
            await emailService.SendPasswordResetAsync(
                recipient, "ANGI Live Test", "live-test-reset-token", TimeSpan.FromMinutes(30), CancellationToken.None);
        }
    }

    public sealed class BrevoLiveFactAttribute : FactAttribute
    {
        public BrevoLiveFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("RUN_BREVO_LIVE_TESTS") != "1" ||
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BREVO_LIVE_TEST_TO")))
            {
                Skip = "Set RUN_BREVO_LIVE_TESTS=1 and BREVO_LIVE_TEST_TO=<email> to send through Brevo.";
            }
        }
    }
}
