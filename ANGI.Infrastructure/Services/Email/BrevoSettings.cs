namespace ANGI.Infrastructure.Services.Email
{
    public sealed class BrevoSettings
    {
        public const string SectionName = "Brevo";

        public string BaseUrl { get; set; } = "https://api.brevo.com/v3/";
        public string ApiKey { get; set; } = string.Empty;
        public string SenderEmail { get; set; } = string.Empty;
        public string SenderName { get; set; } = "ANGI";
        public int TimeoutMilliseconds { get; set; } = 10000;
    }
}
