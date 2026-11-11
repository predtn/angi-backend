namespace ANGI.WebApi.Configs
{
    public sealed class ForwardedHeadersSettings
    {
        public const string SectionName = "ForwardedHeaders";

        /// <summary>True only behind a proxy that sets X-Forwarded-For (Render); off locally, where clients connect directly.</summary>
        public bool Enabled { get; set; }

        /// <summary>How many proxies to trust from the right of X-Forwarded-For: 1 for Render alone.</summary>
        public int ForwardLimit { get; set; } = 1;
    }
}
