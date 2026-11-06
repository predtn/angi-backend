namespace ANGI.WebApi.Configs
{
    public sealed class RateLimitSettings
    {
        public const string SectionName = "RateLimit";

        public int GeneralPermitLimit { get; set; } = 100;
        public int GeneralWindowSeconds { get; set; } = 60;
        public int LoginPermitLimit { get; set; } = 5;
        public int LoginWindowMinutes { get; set; } = 15;
        public int SensitiveAuthPermitLimit { get; set; } = 1;
        public int SensitiveAuthWindowSeconds { get; set; } = 60;

        /// <summary>
        /// Per-IP cap on forgot-password and resend across all emails: the per-email limit alone let one client send
        /// mail to any number of addresses (spam, Brevo quota).
        /// </summary>
        public int SensitiveAuthIpPermitLimit { get; set; } = 5;
        public int SensitiveAuthIpWindowMinutes { get; set; } = 15;
    }
}
