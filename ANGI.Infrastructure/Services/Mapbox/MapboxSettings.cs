namespace ANGI.Infrastructure.Services.Mapbox
{
    /// <summary>Configuration used by the Mapbox Directions client.</summary>
    public sealed class MapboxSettings
    {
        public const string SectionName = "Mapbox";

        public string BaseUrl { get; set; } = "https://api.mapbox.com/";
        public string AccessToken { get; set; } = string.Empty;
        public int TimeoutMilliseconds { get; set; } = 3000;
        public int CacheHours { get; set; } = 24;
    }
}
