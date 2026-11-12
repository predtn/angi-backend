namespace ANGI.Application.Common.Models.Discovery;

/// <summary>
/// The ILIKE pattern of the public province filters (DISH-01, DISH-03, HOME, RM-06, RM-10, REST-01): the province catalog
/// names "Thành phố Hà Nội" while a restaurant may store "Hà Nội", so the administrative prefix is left out of the search.
/// </summary>
public static class ProvinceSearch
{
    private static readonly string[] AdministrativePrefixes = ["Thành phố ", "Tỉnh "];

    /// <summary>A "contains" pattern on the province's own name, which matches both the short and the full spelling.</summary>
    public static string Pattern(string province) => LikePattern.Contains(ShortName(province));

    /// <summary>True when two spellings name the same province ("Thành phố Hà Nội" and "Hà Nội"), ignoring case.</summary>
    public static bool Same(string? left, string? right) =>
        left is not null && right is not null && string.Equals(ShortName(left), ShortName(right), StringComparison.OrdinalIgnoreCase);

    private static string ShortName(string province)
    {
        var name = province.Trim();
        var prefix = AdministrativePrefixes.FirstOrDefault(value => name.StartsWith(value, StringComparison.OrdinalIgnoreCase));
        return prefix is null ? name : name[prefix.Length..].Trim();
    }
}
