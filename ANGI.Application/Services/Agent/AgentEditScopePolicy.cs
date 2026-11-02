using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ANGI.Application.Common.Models.Agent;

namespace ANGI.Application.Services.Agent;

/// <summary>Asks which item to change when an edit names nothing ("đổi cái đó đi"), instead of letting the model guess a whole-plan rewrite.</summary>
public static class AgentEditScopePolicy
{
    private static readonly HashSet<string> Edits = ["doi", "thay", "sua", "chinh", "bo", "xoa"];
    // Chỉ từ đệm và đại từ chỉ định. Câu có thêm ngày, bữa, tên món, "toàn bộ", "món chay"... vẫn do model đọc như cũ.
    private static readonly HashSet<string> Fillers =
    [
        "cai", "mon", "do", "nay", "kia", "ay", "no", "di", "nhe", "nha", "giup", "dum", "toi", "minh", "em", "cho", "lai",
        "khac", "hay", "vay", "thoi", "luon", "a", "ha", "nhi", "duoc", "khong", "voi", "ban", "the"
    ];

    // Chê món mà không nêu món nào ("cái này không ngon"): cũng là muốn đổi nhưng chưa rõ đổi gì.
    private static readonly Regex Dislike = new(@"\b(?:khong (?:ngon|thich|hop|on|ung)|(?:te|chan|dat|do) (?:qua|lam))\b", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> DislikeWords = ["khong", "ngon", "thich", "hop", "on", "ung", "te", "chan", "dat", "do", "qua", "lam"];
    private static readonly Regex Acknowledgement = new(@"^(?:ok|oke|okay|okie|u|uh|um|vang|da|duoc|thoi|hm+|roi|on|tot)(?: (?:roi|nhe|nha|the|day|do|a))*$", RegexOptions.CultureInvariant);

    /// <summary>Returns the fixed edit-scope question for an edit without any target while a plan exists, or null.</summary>
    public static string? Question(AgentContext context)
    {
        // Người dùng đã chọn món trên giao diện hoặc chưa có lịch nào để sửa: không áp dụng.
        if (context.Source is null && context.Roadmap is null || context.Request.SelectedItemKey is not null ||
            context.Request.ConfirmedState?.FocusDay is not null || context.State.FocusDay.HasValue) return null;
        var text = Normalize(context.Request.Content);
        var words = Regex.Split(text, @"[^a-z0-9]+").Where(word => word.Length > 0).ToArray();
        if (words.Length == 0) return null;
        var bareEdit = words.Any(Edits.Contains) && words.All(word => Edits.Contains(word) || Fillers.Contains(word));
        var bareDislike = Dislike.IsMatch(text) && words.All(word => DislikeWords.Contains(word) || Fillers.Contains(word));
        return bareEdit || bareDislike ? AgentReplyBuilder.Clarify(context, "edit_scope") : null;
    }

    /// <summary>True when a name is only pointing words ("cái này", "món đó"), so it cannot be a catalog lookup.</summary>
    public static bool IsPointingOnly(string name)
    {
        var words = Regex.Split(Normalize(name), @"[^a-z0-9]+").Where(word => word.Length > 0).ToArray();
        return words.Length > 0 && words.All(Fillers.Contains);
    }

    /// <summary>True for a bare "ok"/"thôi"/"hmm" over an existing plan with no question open: nothing to plan, so no LLM call is needed.</summary>
    public static bool IsBareAcknowledgement(AgentContext context) =>
        context.Source is not null && context.State.PendingQuestion is null && context.State.PendingStateChange is null &&
        context.State.PendingPlanningRequest is null && context.Request.SelectedItemKey is null &&
        Acknowledgement.IsMatch(Regex.Replace(Normalize(context.Request.Content), @"[^a-z0-9 ]+", " ").Trim());

    private static string Normalize(string text) => new string(text.Normalize(NormalizationForm.FormD)
        .Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark).ToArray())
        .Normalize(NormalizationForm.FormC).ToLowerInvariant().Replace('đ', 'd');
}
