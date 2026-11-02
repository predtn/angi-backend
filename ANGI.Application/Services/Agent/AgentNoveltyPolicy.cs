using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ANGI.Application.Common.Models.Agent;

namespace ANGI.Application.Services.Agent;

/// <summary>
/// Asks what "món lạ / món mới / món chưa ăn" means before the model can turn it into a dish name or an invented filter.
/// No catalog tag means "new"; the only real filter is differentFromPlan, and ANGI has no eating history.
/// </summary>
public static class AgentNoveltyPolicy
{
    // Câu có dấu: so đúng chữ, nên "chưa" không phải "chua", "lạ" không phải "là"; "mới" chỉ tính khi đi liền "món".
    private const string AccentedWording = @"\bchưa\s+(?:từng\s+|bao giờ\s+)?(?:ăn|thử)\b|\b(?:ăn|thử)\s+bao giờ\s+chưa\b|" +
        @"\bmón(?:\s+\p{L}+){0,2}\s+lạ\b|\blạ\s+miệng\b|\bđộc\s+lạ\b|\bmón\s+mới\b";
    // Câu không dấu chỉ bắt cụm ít nhập nhằng: "mon la" có thể là "món là" nên không bắt.
    private const string UnaccentedWording = @"\bchua\s+(?:tung\s+|bao gio\s+)?(?:an|thu)\b|\bla mieng\b|\bdoc la\b|\bmon moi\b";
    // Đã nói rõ là khác lịch hiện tại: model dùng differentFromPlan, không cần hỏi.
    private const string DifferentFromPlan = @"\bkhac\s+(?:(?:voi|cac|nhung|het)\s+)*(?:mon\s+)?(?:(?:trong|o|cua)\s+)?(?:lich|lich trinh|ke hoach|de xuat)\b|\bkhong\s+trung\b";
    private const string NeverEaten = @"\bchua\s+(?:tung\s+|bao gio\s+)?(?:an|thu)\b";

    /// <summary>Returns the fixed novelty question, the no-history answer to that question, or null when the model should read the message.</summary>
    public static string? Question(AgentContext context)
    {
        var text = Normalize(context.Request.Content);
        // Trả lời câu hỏi novelty: chọn "chưa từng ăn" thì nói thẳng là không có dữ liệu thay vì để model lọc bừa;
        // chọn "khác lịch hiện tại" hoặc câu khác thì model xử lý như cũ.
        if (context.State.PendingQuestion?.StartsWith(AgentReplyBuilder.NoveltyQuestion, StringComparison.Ordinal) == true)
            return Regex.IsMatch(text, NeverEaten, RegexOptions.CultureInvariant) && !Regex.IsMatch(text, DifferentFromPlan, RegexOptions.CultureInvariant)
                ? context.Source is null && context.Roadmap is null ? AgentReplyBuilder.NoveltyWithoutHistoryOrPlan : AgentReplyBuilder.NoveltyWithoutHistory
                : null;
        var accented = Accented(context.Request.Content);
        var wording = accented != text
            ? Regex.Match(accented, AccentedWording, RegexOptions.CultureInvariant)
            : Regex.Match(text, UnaccentedWording, RegexOptions.CultureInvariant);
        if (!wording.Success || Regex.IsMatch(text, DifferentFromPlan, RegexOptions.CultureInvariant) || Negated(text, accented, wording.Index)) return null;
        return AgentReplyBuilder.NoveltyQuestion;
    }

    // "Không cần món lạ", "đừng chọn món mới": người dùng đang từ chối món lạ, không cần hỏi nghĩa của nó.
    private static bool Negated(string text, string accented, int index)
    {
        if (accented.Length != text.Length) return false;
        var prefix = text[..index];
        var clause = prefix[(prefix.LastIndexOfAny([',', ';', '.', '!', '?', '\n']) + 1)..];
        return Regex.IsMatch(clause, @"\b(?:khong|dung|chang|khoi)\b", RegexOptions.CultureInvariant);
    }

    private static string Normalize(string text) => new string(Unquote(text).Normalize(NormalizationForm.FormD)
        .Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark).ToArray())
        .Normalize(NormalizationForm.FormC).ToLowerInvariant().Replace('đ', 'd').Trim();

    // Cùng độ dài với Normalize cho chữ tiếng Việt dựng sẵn, nên dùng chung vị trí khớp.
    private static string Accented(string text) => Unquote(text).Normalize(NormalizationForm.FormC).ToLowerInvariant().Trim();

    private static string Unquote(string text) => Regex.Replace(text, "\"[^\"]*\"|'[^']*'|“[^”]*”|‘[^’]*’|`[^`]*`", " ");
}
