using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ANGI.Infrastructure.Services.Email
{
    /// <summary>Sends the account emails through the Brevo transactional API.</summary>
    public sealed class EmailService : IEmailService
    {
        private const string UnavailableMessage = "Không gửi được email. Vui lòng thử lại sau.";

        private readonly HttpClient _httpClient;
        private readonly BrevoSettings _brevoSettings;
        private readonly FrontendSettings _frontendSettings;
        private readonly ILogger<EmailService> _logger;

        public EmailService(
            HttpClient httpClient,
            IOptions<BrevoSettings> brevoSettings,
            IOptions<FrontendSettings> frontendSettings,
            ILogger<EmailService> logger)
        {
            _httpClient = httpClient;
            _brevoSettings = brevoSettings.Value;
            _frontendSettings = frontendSettings.Value;
            _logger = logger;
        }

        public Task SendEmailVerificationAsync(
            string toEmail,
            string displayName,
            string token,
            TimeSpan validFor,
            CancellationToken ct) =>
            SendAsync(toEmail, displayName, token, validFor, "verify-email", EmailTemplates.EmailVerification, ct);

        public Task SendPasswordResetAsync(
            string toEmail,
            string displayName,
            string token,
            TimeSpan validFor,
            CancellationToken ct) =>
            SendAsync(toEmail, displayName, token, validFor, "reset-password", EmailTemplates.PasswordReset, ct);

        private async Task SendAsync(
            string toEmail,
            string displayName,
            string token,
            TimeSpan validFor,
            string frontendPath,
            Func<string, string, TimeSpan, EmailContent> template,
            CancellationToken ct)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(toEmail);
            ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
            ArgumentException.ThrowIfNullOrWhiteSpace(token);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(validFor, TimeSpan.Zero);

            if (!TryGetFrontendBaseUrl(out var frontendBaseUrl) ||
                string.IsNullOrWhiteSpace(_brevoSettings.ApiKey) ||
                string.IsNullOrWhiteSpace(_brevoSettings.SenderEmail) ||
                _httpClient.BaseAddress is null)
            {
                _logger.LogError(
                    "Email is not configured: set Brevo:BaseUrl, Brevo:ApiKey, Brevo:SenderEmail and Frontend:BaseUrl.");
                throw new ServiceUnavailableException("SERVICE_UNAVAILABLE", UnavailableMessage);
            }

            var link = $"{frontendBaseUrl}/{frontendPath}?token={Uri.EscapeDataString(token)}";
            var content = template(displayName, link, validFor);
            var body = new BrevoEmailRequest(
                new BrevoContact(_brevoSettings.SenderEmail, _brevoSettings.SenderName),
                [new BrevoContact(toEmail, displayName)],
                content.Subject,
                content.HtmlContent,
                content.TextContent);

            using var request = new HttpRequestMessage(HttpMethod.Post, "smtp/email")
            {
                Content = JsonContent.Create(body)
            };
            request.Headers.Add("api-key", _brevoSettings.ApiKey);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            try
            {
                using var response = await _httpClient.SendAsync(request, ct);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }

                // Log Brevo's error code only: its message may repeat the recipient.
                _logger.LogError(
                    "Brevo rejected the email with status {StatusCode} and code {BrevoCode}.",
                    (int)response.StatusCode,
                    await ReadErrorCodeAsync(response, ct));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger.LogError("Brevo did not answer within {TimeoutMilliseconds} ms.", _brevoSettings.TimeoutMilliseconds);
            }
            catch (HttpRequestException exception)
            {
                _logger.LogError(exception, "Brevo could not be reached.");
            }

            throw new ServiceUnavailableException("SERVICE_UNAVAILABLE", UnavailableMessage);
        }

        private bool TryGetFrontendBaseUrl(out string baseUrl)
        {
            baseUrl = _frontendSettings.BaseUrl.TrimEnd('/');
            return Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
        }

        private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response, CancellationToken ct)
        {
            try
            {
                var error = await response.Content.ReadFromJsonAsync<BrevoError>(cancellationToken: ct);
                return error?.Code;
            }
            catch (Exception exception) when (exception is JsonException or NotSupportedException)
            {
                return null;
            }
        }

        // Serialized camelCase by JsonContent: sender, to, subject, htmlContent, textContent.
        private sealed record BrevoEmailRequest(
            BrevoContact Sender,
            IReadOnlyList<BrevoContact> To,
            string Subject,
            string HtmlContent,
            string TextContent);

        private sealed record BrevoContact(string Email, string Name);

        private sealed record BrevoError(string? Code);
    }
}
