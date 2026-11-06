namespace ANGI.WebApi.Configs
{
    public sealed class TrustedClientIpSettings
    {
        public const string SectionName = "TrustedClientIp";

        /// <summary>
        /// Secret shared with the Next.js server (env BACKEND_PROXY_KEY there). The frontend calls the API from its
        /// server, so without it every guest has the Vercel server's IP. Empty disables the headers (local, tests).
        /// </summary>
        public string ProxyKey { get; set; } = string.Empty;
    }
}
