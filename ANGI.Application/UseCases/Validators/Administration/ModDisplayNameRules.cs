using System.Buffers;
using System.Globalization;
using System.Text;

namespace ANGI.Application.UseCases.Validators.Administration;

/// <summary>Provides consistent Unicode display-name checks to MOD request validators.</summary>
internal static class ModDisplayNameRules
{
    /// <summary>Counts Unicode scalar values so supplementary characters count as one character.</summary>
    public static int GetUnicodeLength(string value) => value.EnumerateRunes().Count();

    /// <summary>Rejects unsafe or malformed Unicode while preserving ordinary names and emoji.</summary>
    public static bool ContainsOnlySafeUnicode(string? value)
    {
        if (value is null)
        {
            return false;
        }

        var remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out var rune, out var consumed) != OperationStatus.Done ||
                rune.Value == Rune.ReplacementChar.Value)
            {
                return false;
            }

            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or
                UnicodeCategory.Format or
                UnicodeCategory.PrivateUse or
                UnicodeCategory.OtherNotAssigned or
                UnicodeCategory.LineSeparator or
                UnicodeCategory.ParagraphSeparator)
            {
                return false;
            }

            remaining = remaining[consumed..];
        }

        return true;
    }
}
