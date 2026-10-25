using System.Text.RegularExpressions;

namespace ANGI.Application.UseCases.Validators.Restaurant;

/// <summary>Checks Vietnamese mobile and geographic landline contact numbers for owner restaurant requests.</summary>
public static partial class VietnamesePhoneNumberRules
{
    /// <summary>
    /// Accepts a domestic 0 or international +84 prefix, supported mobile or area-code ranges,
    /// and single spaces, dots, or hyphens between digit groups within the 20-character API limit.
    /// Checks number format only; it does not establish subscriber ownership or reachability.
    /// </summary>
    public static bool IsValid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 20)
        {
            return false;
        }

        var formatted = value.Trim(' ');
        if (!PresentationPattern().IsMatch(formatted))
        {
            return false;
        }

        var number = formatted.Replace(" ", "").Replace(".", "").Replace("-", "");
        return NumberPattern().IsMatch(number);
    }

    /// <summary>Requires an intact domestic or country prefix and one separator between ASCII digit groups.</summary>
    [GeneratedRegex(@"\A(?:0[0-9]+|\+84[ .-]?[0-9]+)(?:[ .-][0-9]+)*\z", RegexOptions.NonBacktracking)]
    private static partial Regex PresentationPattern();

    /// <summary>Matches nine-digit mobile or ten-digit geographic national numbers after 0 or +84.</summary>
    [GeneratedRegex(
        @"\A(?:0|\+84)(?:(?:3[2-9]|5[125689]|7[06789]|8[1-9]|9[0-9])[0-9]{7}|2(?:[48][0-9]|0[3-9]|1[0-689]|2[0-25-9]|3[2-9]|5[124-9]|6[0-39]|7[0-7]|9[0-4679])[0-9]{7})\z",
        RegexOptions.NonBacktracking)]
    private static partial Regex NumberPattern();
}
