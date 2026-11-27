namespace ANGI.WebApi.Configs
{
    public sealed class ForwardedHeadersSettings
    {
        public const string SectionName = "ForwardedHeaders";

        /// <summary>
        /// True only behind a proxy that sets X-Forwarded-For. Off locally and in the self-hosted deployment, where only
        /// the frontend container calls the API (the browser IP comes through TrustedClientIp instead).
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>How many proxies to trust from the right of X-Forwarded-For: 1 for a single proxy.</summary>
        public int ForwardLimit { get; set; } = 1;
    }
}
