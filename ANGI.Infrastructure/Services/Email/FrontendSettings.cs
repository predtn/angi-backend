namespace ANGI.Infrastructure.Services.Email
{
    /// <summary>Where the links sent by email point to.</summary>
    public sealed class FrontendSettings
    {
        public const string SectionName = "Frontend";

        public string BaseUrl { get; set; } = string.Empty;
    }
}
