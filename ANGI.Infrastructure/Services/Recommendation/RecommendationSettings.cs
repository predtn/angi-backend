namespace ANGI.Infrastructure.Services.Recommendation
{
    public sealed class RecommendationSettings
    {
        public const string SectionName = "Recommendation";

        public string BaseUrl { get; set; } = string.Empty;
        public string ApiKey { get; set; } = string.Empty;
        public int TimeoutMilliseconds { get; set; } = 800;
        /// <summary>Agent conversation ranking runs in the background of a chat turn, so it may wait longer than list screens.</summary>
        public int AgentTimeoutMilliseconds { get; set; } = 3000;
    }
}
