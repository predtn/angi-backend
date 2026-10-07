using System.Net.Http.Json;
using System.Text.Json.Serialization;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Infrastructure.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ANGI.Infrastructure.Services;

public class EmailService : IEmailService
{
    private readonly HttpClient _httpClient;
    private readonly BrevoSettings _settings;
    private readonly ILogger<EmailService> _logger;

    public EmailService(
        HttpClient httpClient,
        IOptions<BrevoSettings> options,
        ILogger<EmailService> logger)
    {
        _httpClient = httpClient;
        _settings = options.Value;
        _logger = logger;
    }

    private async Task<bool> SendEmailWithTemplateAsync(string toEmail, string toName, int templateId, object parameters)
    {
        try
        {
            var payload = new
            {
                to = new[] { new { email = toEmail, name = toName } },
                templateId = templateId,
                @params = parameters
            };

            var response = await _httpClient.PostAsJsonAsync("smtp/email", payload);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError("Failed to send template {TemplateId} via Brevo. Status: {StatusCode}, Error: {ErrorContent}", templateId, response.StatusCode, errorContent);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception occurred while sending template {TemplateId} to {Email}", templateId, toEmail);
            return false;
        }
    }

    public async Task<bool> SendVerificationEmailAsync(string toEmail, string toName, string verificationLink)
    {
        var parameters = new { userName = toName, verifyLink = verificationLink };
        return await SendEmailWithTemplateAsync(toEmail, toName, _settings.VerificationTemplateId, parameters);
    }

    public async Task<bool> SendPasswordResetEmailAsync(string toEmail, string toName, string resetLink)
    {
        var parameters = new { userName = toName, resetLink = resetLink };
        return await SendEmailWithTemplateAsync(toEmail, toName, _settings.PasswordResetTemplateId, parameters);
    }
}
