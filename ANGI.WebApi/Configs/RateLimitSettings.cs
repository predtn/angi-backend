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
    }
}
