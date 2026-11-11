using ANGI.Application.Common.Interfaces.Services.Social;
using Ganss.Xss;

namespace ANGI.Infrastructure.Services.Social;

/// <summary>Uses an allowlist HTML parser to remove executable markup from user-authored blog content.</summary>
public sealed class BlogContentSanitizer : IBlogContentSanitizer
{
    private readonly HtmlSanitizer _sanitizer;

    /// <summary>Creates a reusable sanitizer restricted to web-safe link protocols.</summary>
    public BlogContentSanitizer()
    {
        _sanitizer = new HtmlSanitizer();
        _sanitizer.AllowedSchemes.Clear();
        _sanitizer.AllowedSchemes.Add("http");
        _sanitizer.AllowedSchemes.Add("https");
        _sanitizer.AllowedSchemes.Add("mailto");
    }

    /// <summary>Parses the fragment and removes scripts, event handlers, and unsafe resource URLs.</summary>
    public string Sanitize(string content) => _sanitizer.Sanitize(content);
}
