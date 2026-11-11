namespace ANGI.Application.Common.Models;

/// <summary>
/// "Contains" patterns for PostgreSQL ILIKE built from user input. % and _ are wildcards in LIKE, so a search for "50%"
/// or "a_b" would match far more than typed; they are escaped with <see cref="Escape"/>, which every ILIKE call that
/// uses these patterns passes as its escape character.
/// </summary>
public static class LikePattern
{
    /// <summary>The escape character to pass to EF.Functions.ILike(value, pattern, LikePattern.Escape).</summary>
    public const string Escape = "\\";

    /// <summary>Matches values containing <paramref name="text"/> literally (trimmed).</summary>
    public static string Contains(string text) => $"%{Literal(text.Trim())}%";

    /// <summary>Escapes the escape character, % and _ so they match themselves.</summary>
    public static string Literal(string text) => text
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);
}
