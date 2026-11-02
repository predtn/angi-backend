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

    /// <summary>Returns the fixed edit-scope question for an edit without any target while a plan exists, or null.</summary>
    public static string? Question(AgentContext context)
    {
        // Người dùng đã chọn món trên giao diện hoặc chưa có lịch nào để sửa: không áp dụng.
        if (context.Source is null && context.Roadmap is null || context.Request.SelectedItemKey is not null ||
            context.Request.ConfirmedState?.FocusDay is not null || context.State.FocusDay.HasValue) return null;
        var words = Regex.Split(Normalize(context.Request.Content), @"[^a-z0-9]+").Where(word => word.Length > 0).ToArray();
        if (words.Length == 0 || !words.Any(Edits.Contains) || !words.All(word => Edits.Contains(word) || Fillers.Contains(word))) return null;
        return AgentReplyBuilder.Clarify(context, "edit_scope");
    }

    private static string Normalize(string text) => new string(text.Normalize(NormalizationForm.FormD)
        .Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark).ToArray())
        .Normalize(NormalizationForm.FormC).ToLowerInvariant().Replace('đ', 'd');
}
