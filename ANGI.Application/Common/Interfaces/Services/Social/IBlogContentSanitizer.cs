namespace ANGI.Application.Common.Interfaces.Services.Social;

/// <summary>Sanitizes user-authored blog HTML before it crosses the persistence boundary.</summary>
public interface IBlogContentSanitizer
{
    /// <summary>Removes executable or otherwise unsafe markup while retaining safe article formatting.</summary>
    string Sanitize(string content);
}
