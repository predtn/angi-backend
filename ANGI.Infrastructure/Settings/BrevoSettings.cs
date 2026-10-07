namespace ANGI.Infrastructure.Settings;

public class BrevoSettings
{
    public const string SectionName = "Brevo";
    public string ApiKey { get; set; } = string.Empty;
    public string SenderEmail { get; set; } = string.Empty;
    public string SenderName { get; set; } = string.Empty;

    public int VerificationTemplateId { get; set; }
    public int PasswordResetTemplateId { get; set; }
}
