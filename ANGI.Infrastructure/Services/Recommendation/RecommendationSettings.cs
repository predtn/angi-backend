namespace ANGI.Infrastructure.Services.Recommendation
{
    public sealed class RecommendationSettings
    {
        public const string SectionName = "Recommendation";

        public string BaseUrl { get; set; } = string.Empty;
        public string ApiKey { get; set; } = string.Empty;
        public int TimeoutMilliseconds { get; set; } = 800;
    }
}
