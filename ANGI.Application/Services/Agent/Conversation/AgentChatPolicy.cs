using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.UseCases.Validators.Agent;

namespace ANGI.Application.Services.Agent.Conversation;

/// <summary>Authorizes explicit chat controls independently of model output, quoted data or generic agreement.</summary>
public static class AgentChatPolicy
{
    // Cách trả lời có/không thường gặp, kể cả viết tắt và tiếng lóng. Chỉ có hiệu lực khi đang có đúng một câu hỏi chờ trả lời, nên không tốn lượt gọi LLM.
    private const string Yes = @"ok|oke|okay|okie|oki|okla|okela|u|uh|uhm|um|uk|o|vang|da|co|dung|dung roi|dung vay|duoc|dc|dong y|xac nhan|yes|yep|yeah|yup|chuan|chuan roi|cung duoc|sao cung duoc|tuyet|👍";
    private const string No = @"khong|ko|k|kg|hong|hok|khum|no|nope|thoi|khoi|thoi khoi|khoan|chua|huy|giu nguyen|khong dong y|khong doi|khong can";
    private const string YesTail = @"(?: (?:roi|nhe|nha|nhen|luon|lun|di|a|nao|the|vay|do|ban))*";
    private const string NoTail = @"(?: (?:da|luu|nua|nhe|nha|di|dau|a|thoi|ban))*";

    /// <summary>Recognizes a complete save instruction; negation, quotes, questions and generic yes never commit.</summary>
    public static bool IsSaveRequest(string content) => Regex.IsMatch(Normalize(content),
        @"^(?:(?:toi (?:muon|xac nhan)|hay|dong y) )?(?:luu (?:lich trinh|lich|ban nhap|de xuat)(?: nay| hien tai)?|chot (?:lich trinh|lich|de xuat)(?: nay| hien tai)?|xac nhan luu(?: lich trinh| lich)?(?: nay)?|save (?:this )?(?:itinerary|plan))(?: giup toi| cho toi| nhe| di)?$",
        RegexOptions.CultureInvariant);

    /// <summary>Recognizes a bare save intent ("lưu", "chốt đi"); it is answered with the exact save wording, never a roadmap write or a new plan.</summary>
    public static bool IsShortSaveRequest(string content) => Regex.IsMatch(Normalize(content),
        @"^(?:(?:ok|vay|thoi|roi) )?(?:luu|chot|save)(?: (?:lai|di|nhe|nha|luon|nao|giup|dum|cho toi|giup toi))*$",
        RegexOptions.CultureInvariant);

    /// <summary>Requests a complete review before saving; this wording never authorizes a roadmap write.</summary>
    public static bool IsReviewRequest(string content) => Regex.IsMatch(Normalize(content),
        @"^(?:hay )?(?:lap|tong hop|hoan thien) (?:lich|lich trinh)(?: giup toi| cho toi)? de chot(?: lich| lich trinh)?$",
        RegexOptions.CultureInvariant);

    /// <summary>"Ok lên lịch", "lên lịch đi": a go-ahead that means saving only while a ready proposal exists; without one it is a planning request.</summary>
    public static bool IsGoAheadRequest(string content) => Regex.IsMatch(Normalize(content),
        @"^(?:(?:ok|oke|okay|vay|thoi|roi) )?(?:len lich|dat lich|chot lich)(?: (?:di|nhe|nha|luon|nao|giup|dum|cho toi|giup toi))*$",
        RegexOptions.CultureInvariant);

    /// <summary>A short yes to "Mình lưu lịch này nhé?"; it saves only because that question named this exact proposal version.</summary>
    public static bool AcceptsSave(string content) => IsShortSaveRequest(content) || IsGoAheadRequest(content) || IsYes(content);

    /// <summary>Backs out of an open save question; the proposal stays as it is.</summary>
    public static bool DeclinesSave(string content) => IsNo(content) || Regex.IsMatch(Normalize(content), @"^dung luu(?: (?:nhe|nha|nua))*$", RegexOptions.CultureInvariant);

    /// <summary>Saves without a start date because the user does not know it yet; the roadmap's date stays open.</summary>
    public static bool DefersStartDate(string content) => Regex.IsMatch(Normalize(content),
        @"^(?:de sau|chon sau|chua biet|khong biet|chua chon|chua co ngay|bo qua|khong can|khong can ngay|luu luon)(?: (?:ngay|nhe|nha|di|luon))*$", RegexOptions.CultureInvariant);

    /// <summary>Recognizes a request to see the current plan, the only question answered by listing it again.</summary>
    public static bool IsPlanViewRequest(string content) => Regex.IsMatch(Normalize(content),
        @"\b(?:xem|in|liet ke|hien thi|tong hop)\b.*\b(?:lich|lo trinh|roadmap|ke hoach|de xuat|ban nhap)\b|\b(?:lich|lo trinh|roadmap|ke hoach)\b.*\b(?:co gi|the nao|ra sao|hien tai)\b",
        RegexOptions.CultureInvariant);

    /// <summary>Allows a short affirmative only when a validated pending constraint change exists.</summary>
    public static bool AcceptsChange(string content) => IsYes(content) ||
        Regex.IsMatch(Normalize(content), $@"^(?:(?:xac nhan|dong y) thay doi|doi|quat|trien|tien hanh|lam|thay doi){YesTail}$", RegexOptions.CultureInvariant);

    /// <summary>Rejects the pending constraint change without discarding the previously valid preview.</summary>
    public static bool RejectsChange(string content) => IsNo(content) ||
        Regex.IsMatch(Normalize(content), $@"^huy thay doi{NoTail}$", RegexOptions.CultureInvariant);

    /// <summary>Declines an open clarification over an existing plan: nothing changes and the plan stays savable.</summary>
    public static bool DismissesQuestion(string content) => IsNo(content);

    /// <summary>A whole-message yes. "Dừng" loses its accents to the same letters as "đúng" and is the opposite answer, so it is told apart before normalizing.</summary>
    private static bool IsYes(string content) => !content.Contains("dừng", StringComparison.OrdinalIgnoreCase) &&
        Regex.IsMatch(Normalize(content), $@"^(?:{Yes}){YesTail}$", RegexOptions.CultureInvariant);

    private static bool IsNo(string content) => Regex.IsMatch(Normalize(content), $@"^(?:{No}){NoTail}$", RegexOptions.CultureInvariant);

    /// <summary>Names what still blocks saving, so the user is not left guessing which question is meant.</summary>
    public static string SaveBlocked(AgentState state) => state.PendingQuestion is { } question && state.PendingStateChange is null && state.PendingPlanningRequest is null
        ? $"Trước khi lưu, mình cần bạn trả lời: {question} Nếu không muốn đổi gì, bạn nhắn “giữ nguyên” rồi bảo mình chốt nhé."
        : "Bạn cần trả lời câu hỏi hoặc xác nhận thay đổi điều kiện đang chờ trước khi lưu lịch.";

    /// <summary>Resolves unambiguous draft controls before a provider call; other natural phrases remain model-interpreted.</summary>
    public static string? DraftControl(string content) => Normalize(content) switch
    {
        "bo ban nhap" or "bo nhap" or "huy ban nhap" or "bo de xuat" or "bo ban chinh sua" or "discard draft" => "discard",
        "lam lai tu dau" or "bat dau lai tu dau" or "bat dau ke hoach moi" or "reset" or "start over" => "reset",
        _ => null
    };

    /// <summary>Stages validated proposed constraints, retaining the original state until the user confirms them.</summary>
    public static AgentPreparedTurn StageChange(AgentState current, AgentState proposed, bool hasPlan = true)
    {
        // STEP-01: Prepare changes as List<string>.
        var changes = new List<string>();
        // STEP-02: When current.Province is not proposed.Province, call changes.Add.
        if (current.Province != proposed.Province) changes.Add($"địa điểm: {proposed.Province ?? "chưa chọn"}");
        // STEP-03: When current.NumDays is not proposed.NumDays, call changes.Add.
        if (current.NumDays != proposed.NumDays) changes.Add($"số ngày: {proposed.NumDays?.ToString(CultureInfo.InvariantCulture) ?? "chưa chọn"}");
        // STEP-04: When current.BudgetAmount is not proposed.BudgetAmount or current.BudgetUnrestricted is not proposed.BudgetUnrestricted, call changes.Add.
        if (current.BudgetAmount != proposed.BudgetAmount || current.BudgetUnrestricted != proposed.BudgetUnrestricted)
            changes.Add(proposed.BudgetUnrestricted ? "ngân sách không giới hạn" : $"ngân sách: {proposed.BudgetAmount?.ToString("0", CultureInfo.InvariantCulture) ?? "chưa chọn"} đồng");
        // STEP-05: When current.MaxPricePerMeal is not proposed.MaxPricePerMeal, call changes.Add.
        if (current.MaxPricePerMeal != proposed.MaxPricePerMeal) changes.Add($"trần giá mỗi món: {proposed.MaxPricePerMeal?.ToString("0", CultureInfo.InvariantCulture) ?? "không giới hạn"} đồng");
        // STEP-06: When current.TagIds.SequenceEqual(proposed.TagIds) does not hold or current.ExcludeTagIds.SequenceEqual(proposed.ExcludeTagIds) does not hold, call changes.Add.
        if (!current.TagIds.SequenceEqual(proposed.TagIds) || !current.ExcludeTagIds.SequenceEqual(proposed.ExcludeTagIds)) changes.Add("bộ lọc món ăn");
        // STEP-07: Add the built entries to changes.
        changes.AddRange(MealRequestChanges(current.MealRequests, proposed.MealRequests));
        // STEP-08: When current.RadiusKm is not proposed.RadiusKm, current.OriginMode is not proposed.OriginMode and more.
        if (current.RadiusKm != proposed.RadiusKm || current.OriginMode != proposed.OriginMode || current.OriginLatitude != proposed.OriginLatitude || current.OriginLongitude != proposed.OriginLongitude)
            changes.Add("điểm xuất phát hoặc bán kính tìm món");
        // STEP-09: Prepare reply from string.Join and pending.
        // Chưa có lịch xem trước/đã lưu thì không nhắc "bản lịch chưa thay đổi" để người dùng không tưởng đã có lịch.
        var reply = $"Mình đổi {string.Join("; ", changes)} nhé? Bạn nhắn “ok” để đổi hoặc “thôi” để giữ nguyên." +
            (hasPlan ? " Bản lịch hiện tại chưa thay đổi." : "");
        var pending = proposed with { PendingStateChange = null, PendingQuestion = null, PendingPlanningRequest = null,
            FocusDay = current.FocusDay, FocusMeal = current.FocusMeal,
            SourceRoadmapUpdatedAt = current.SourceRoadmapUpdatedAt, HistoryStartSequence = current.HistoryStartSequence };
        // STEP-10: Return the new result.
        return new(reply, current with { PendingQuestion = reply, PendingStateChange = pending, PendingPlanningRequest = null }, null, null);
    }

    /// <summary>Names each trip/day/meal scope whose dish or restaurant request was added, removed or changed.</summary>
    private static IEnumerable<string> MealRequestChanges(AgentMealRequest[] current, AgentMealRequest[] proposed)
    {
        // STEP-01: Prepare before through current.ToDictionary and after through proposed.ToDictionary.
        var before = current.ToDictionary(x => (x.DayNumber, x.MealSlot));
        var after = proposed.ToDictionary(x => (x.DayNumber, x.MealSlot));
        // STEP-02: For each key in Keys.Union().OrderBy(filtered).ThenBy(filtered), prepare scope from AgentReplyBuilder.Meal and check whether after does not contain key.
        foreach (var key in before.Keys.Union(after.Keys).OrderBy(x => x.DayNumber ?? 0).ThenBy(x => x.MealSlot is null ? -1 : Array.IndexOf(AgentStateValidator.Meals, x.MealSlot)))
        {
            var scope = key.DayNumber is not { } day ? "toàn chuyến" : key.MealSlot is null ? $"ngày {day}" : $"{AgentReplyBuilder.Meal(key.MealSlot)} ngày {day}";
            if (!after.ContainsKey(key)) yield return $"bỏ yêu cầu món/quán cho {scope}";
            else if (!before.ContainsKey(key)) yield return $"thêm yêu cầu món/quán cho {scope}";
            else if (AgentJson.Write(before[key]) != AgentJson.Write(after[key])) yield return $"đổi yêu cầu món/quán cho {scope}";
        }
    }

    /// <summary>Normalizes Vietnamese accents and terminal emphasis without removing negation, quotes or question marks.</summary>
    private static string Normalize(string content)
    {
        var text = new string(content.Normalize(NormalizationForm.FormD).Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark).ToArray())
            .Normalize(NormalizationForm.FormC).ToLowerInvariant().Replace('đ', 'd').Trim().TrimEnd('.', '!').Trim();
        return Regex.Replace(text, @"\s+", " ");
    }
}
