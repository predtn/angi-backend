namespace ANGI.Infrastructure.Services.Auth
{
    /// <summary>Contains the OAuth client identifier accepted by Google ID-token validation.</summary>
    public sealed class GoogleSettings
    {
        public const string SectionName = "Google";

        public string ClientId { get; set; } = string.Empty;
    }
}
