using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ANGI.Application.Common.Models.Agent;

namespace ANGI.Application.Services.Agent;

/// <summary>Authorizes explicit chat controls independently of model output, quoted data or generic agreement.</summary>
public static class AgentChatPolicy
{
    /// <summary>Recognizes a complete save instruction; negation, quotes, questions and generic yes never commit.</summary>
    public static bool IsSaveRequest(string content) => Regex.IsMatch(Normalize(content),
        @"^(?:(?:toi (?:muon|xac nhan)|hay|dong y) )?(?:luu (?:lich trinh|lich|ban nhap|de xuat)(?: nay| hien tai)?|chot (?:lich trinh|lich|de xuat)(?: nay| hien tai)?|xac nhan luu(?: lich trinh| lich)?(?: nay)?|save (?:this )?(?:itinerary|plan))(?: giup toi| cho toi| nhe| di)?$",
        RegexOptions.CultureInvariant);

    /// <summary>Requests a complete review before saving; this wording never authorizes a roadmap write.</summary>
    public static bool IsReviewRequest(string content) => Regex.IsMatch(Normalize(content),
        @"^(?:hay )?(?:lap|tong hop|hoan thien) (?:lich|lich trinh)(?: giup toi| cho toi)? de chot(?: lich| lich trinh)?$",
        RegexOptions.CultureInvariant);

    /// <summary>Allows a short affirmative only when a validated pending constraint change exists.</summary>
    public static bool AcceptsChange(string content) => Normalize(content) is "dong y" or "xac nhan thay doi" or "dong y thay doi" or "xac nhan" or "ok" or "yes" or "dung roi";

    /// <summary>Rejects the pending constraint change without discarding the previously valid preview.</summary>
    public static bool RejectsChange(string content) => Normalize(content) is "khong" or "khong dong y" or "giu nguyen" or "huy thay doi" or "no";

    /// <summary>Resolves unambiguous draft controls before a provider call; other natural phrases remain model-interpreted.</summary>
    public static string? DraftControl(string content) => Normalize(content) switch
    {
        "bo ban nhap" or "bo nhap" or "huy ban nhap" or "bo de xuat" or "bo ban chinh sua" or "discard draft" => "discard",
        "lam lai tu dau" or "bat dau lai tu dau" or "bat dau ke hoach moi" or "reset" or "start over" => "reset",
        _ => null
    };

    /// <summary>Stages validated proposed constraints, retaining the original state until the user confirms them.</summary>
    public static AgentPreparedTurn StageChange(AgentState current, AgentState proposed)
    {
        var changes = new List<string>();
        if (current.Province != proposed.Province) changes.Add($"địa điểm: {proposed.Province ?? "chưa chọn"}");
        if (current.NumDays != proposed.NumDays) changes.Add($"số ngày: {proposed.NumDays}");
        if (current.BudgetAmount != proposed.BudgetAmount || current.BudgetUnrestricted != proposed.BudgetUnrestricted)
            changes.Add(proposed.BudgetUnrestricted ? "ngân sách không giới hạn" : $"ngân sách: {proposed.BudgetAmount:0} đồng");
        if (current.MaxPricePerMeal != proposed.MaxPricePerMeal) changes.Add($"trần giá mỗi món: {proposed.MaxPricePerMeal?.ToString("0", CultureInfo.InvariantCulture) ?? "không giới hạn"} đồng");
        if (!current.TagIds.SequenceEqual(proposed.TagIds) || !current.ExcludeTagIds.SequenceEqual(proposed.ExcludeTagIds)) changes.Add("bộ lọc món ăn");
        if (current.RadiusKm != proposed.RadiusKm || current.OriginMode != proposed.OriginMode || current.OriginLatitude != proposed.OriginLatitude || current.OriginLongitude != proposed.OriginLongitude)
            changes.Add("điểm xuất phát hoặc bán kính tìm món");
        var reply = $"Bạn xác nhận thay đổi {string.Join("; ", changes)}? Nhắn ‘xác nhận thay đổi’ để áp dụng hoặc ‘giữ nguyên’ để hủy. Bản lịch đang lưu chưa thay đổi.";
        var pending = proposed with { PendingStateChange = null, PendingQuestion = null,
            SourceRoadmapVersion = current.SourceRoadmapVersion, HistoryStartSequence = current.HistoryStartSequence };
        return new(reply, current with { PendingQuestion = reply, PendingStateChange = pending }, null, null);
    }

    /// <summary>Normalizes Vietnamese accents and terminal emphasis without removing negation, quotes or question marks.</summary>
    private static string Normalize(string content)
    {
        var text = new string(content.Normalize(NormalizationForm.FormD).Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark).ToArray())
            .Normalize(NormalizationForm.FormC).ToLowerInvariant().Replace('đ', 'd').Trim().TrimEnd('.', '!').Trim();
        return Regex.Replace(text, @"\s+", " ");
    }
}
