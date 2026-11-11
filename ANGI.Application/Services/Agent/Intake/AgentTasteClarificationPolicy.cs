using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ANGI.Application.Services.Agent.Intake;

/// <summary>
/// Tells whether a message names a taste, as evidence for the backend's checks on what the model proposes.
/// The model asks about level and scope itself; nothing here answers the user.
/// </summary>
public static class AgentTasteClarificationPolicy
{
    private const string Taste = "cay|chua|ngot|man|dang|beo|umami|thanh mat|chat|tuoi|dam da|thom";
    // "thích/muốn ăn cay" và dạng giới hạn "đừng/không cay quá", "bớt cay": cả hai đều chưa nêu rõ mức.
    private const string Preference = @"\b(?:thich|muon|uu tien)(?:\s+(?:an|vi|mon|do|khau vi))*\s+(?:(?:rat|hoi|it|khong)\s+)*(?<taste>" + Taste + @")\b[^,;.!?\n]*" +
        @"|\b(?:dung|khong)\s+(?:an\s+)?(?<taste>" + Taste + @")\s+(?:qua|lam)\b[^,;.!?\n]*|\bbot\s+(?<taste>" + Taste + @")\b[^,;.!?\n]*";
    // Nhãn hiển thị cũng là cách viết có dấu đúng của từ chỉ vị, dùng để phân biệt "chua" với "chưa", "mặn" với "mận".
    private static readonly Dictionary<string, string> Labels = new()
    {
        ["cay"] = "cay", ["chua"] = "chua", ["ngot"] = "ngọt", ["man"] = "mặn",
        ["dang"] = "đắng", ["beo"] = "béo", ["umami"] = "umami", ["thanh mat"] = "thanh mát",
        ["chat"] = "chát", ["tuoi"] = "tươi", ["dam da"] = "đậm đà", ["thom"] = "thơm"
    };

    /// <summary>True when the message itself declares a taste preference ("thích ăn chua"), so its day/meal wording may scope that taste.</summary>
    public static bool StatesTaste(string content) => Declarations(content).Length > 0;

    /// <summary>Display names of the tastes the message names ("cay", "ngọt"), ignoring look-alike words such as "chưa" or "mận".</summary>
    public static string[] MentionedTastes(string content)
    {
        var (text, accented) = (Normalize(content), Accented(content));
        return Regex.Matches(text, @"\b(?:" + Taste + @")\b", RegexOptions.CultureInvariant).Cast<Match>()
            .Where(match => SpelledAsTaste(text, accented, match.Value, match.Index)).Select(match => Labels[match.Value]).Distinct().ToArray();
    }

    private static Match[] Declarations(string content)
    {
        var (text, accented) = (Normalize(content), Accented(content));
        return Regex.Matches(text, Preference, RegexOptions.CultureInvariant).Cast<Match>()
            .Where(match => !Regex.IsMatch(text[..match.Index], @"\b(?:khong|chang|chua|dung|khong he)\s*$", RegexOptions.CultureInvariant) &&
                SpelledAsTaste(text, accented, match.Groups["taste"].Value, match.Groups["taste"].Index)).ToArray();
    }

    // So khớp chạy trên bản bỏ dấu để người gõ không dấu vẫn được hiểu. Khi câu có dấu, từ phải đúng cách viết của vị:
    // "chưa" (not yet) không phải "chua", "mận" không phải "mặn", "đang" không phải "đắng". Câu không dấu giữ cách đọc cũ.
    private static bool SpelledAsTaste(string text, string accented, string taste, int index)
    {
        // STEP-01: Return true early when accented.Length is not text.Length.
        if (accented.Length != text.Length) return true;
        // STEP-02: Prepare word through accented.Substring.
        var word = accented.Substring(index, taste.Length);
        // STEP-03: Return the computed condition.
        return word == Labels[taste] || word == taste && accented == text;
    }

    private static string Normalize(string text)
    {
        return new string(Unquote(text).Normalize(NormalizationForm.FormD)
            .Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark).ToArray())
            .Normalize(NormalizationForm.FormC).ToLowerInvariant().Replace('đ', 'd').Trim();
    }

    // Cùng độ dài với Normalize cho chữ tiếng Việt dựng sẵn (mỗi chữ có dấu ứng với đúng một chữ không dấu), nên dùng chung vị trí.
    private static string Accented(string text) => Unquote(text).Normalize(NormalizationForm.FormC).ToLowerInvariant().Trim();

    private static string Unquote(string text) => Regex.Replace(text, "\"[^\"]*\"|'[^']*'|“[^”]*”|‘[^’]*’|`[^`]*`", " ");
}
