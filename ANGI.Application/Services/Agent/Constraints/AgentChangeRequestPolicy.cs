using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Services.Agent.Intake;
using ANGI.Application.UseCases.Agent;

namespace ANGI.Application.Services.Agent.Constraints;

/// <summary>
/// Detects a model proposal that would change an existing plan although the user asked for nothing ("ăn vui vui", "ok"):
/// no edit wording, no taste, no day/meal, and no new constraint grounded in the user's wording.
/// </summary>
public static class AgentChangeRequestPolicy
{
    // Từ cho thấy người dùng muốn đổi/lập/tìm lại (tiếng Việt và tiếng Anh); đủ rộng để không chặn nhầm yêu cầu sửa thật.
    private const string ChangeWording = @"\b(?:doi|thay|sua|chinh|bo|xoa|them|khac|lai|moi|phuong an|lap lich|len lich|xep|chon|lay|dat|tim|goi y|de xuat|cap nhat|tao|" +
        @"plan|change|replace|swap|update|edit|add|remove|another|different|redo|suggest|recommend|breakfast|lunch|dinner)\b";
    // Chữ chung trong tên tag, không đủ để coi là người dùng đã nhắc tới tag đó ("Món ăn vặt" chỉ có "vặt" là đặc trưng).
    private static readonly HashSet<string> GenericTagWords = ["món", "ăn", "đồ", "và", "&", "-", "/", "ít", "thịt"];

    /// <summary>
    /// True when the message requests no change and every user constraint the accepted state adds is ungrounded in that message,
    /// such as a "Món ăn vặt" tag invented from "ăn vui vui".
    /// </summary>
    public static bool IsUnrequested(string content, AgentState before, AgentState after, IReadOnlyDictionary<short, string> tagNames)
    {
        var text = Normalize(content);
        if (Regex.IsMatch(text, ChangeWording, RegexOptions.CultureInvariant) || AgentTasteClarificationPolicy.MentionedTastes(content).Length > 0)
            return false;
        var references = AgentMealScopePolicy.Read(content, after);
        if (references.Meals.Length > 0 || references.Days.Length > 0 || Regex.IsMatch(text, @"\bbua\s+(?:sang|trua|toi|phu|khuya)\b", RegexOptions.CultureInvariant))
            return false;
        if (SameUserConstraints(before, after)) return true;
        // Thay đổi ngoài tag/yêu cầu món (số ngày, ngân sách, khẩu vị...) do lớp ràng buộc và xác nhận riêng xử lý: không chặn ở đây.
        if (!SameUserConstraints(before with { TagIds = [], ExcludeTagIds = [], MealRequests = [] }, after with { TagIds = [], ExcludeTagIds = [], MealRequests = [] }))
            return false;
        var beforeTags = before.TagIds.Concat(before.ExcludeTagIds).Concat(before.MealRequests.SelectMany(x => x.TagIds)).ToHashSet();
        var addedTags = after.TagIds.Concat(after.ExcludeTagIds).Concat(after.MealRequests.SelectMany(x => x.TagIds)).Where(id => !beforeTags.Contains(id)).Distinct().ToArray();
        var beforeNames = before.MealRequests.SelectMany(x => new[] { x.DishName, x.RestaurantName }).OfType<string>().Select(Normalize).ToHashSet(StringComparer.Ordinal);
        var addedNames = after.MealRequests.SelectMany(x => new[] { x.DishName, x.RestaurantName }).OfType<string>().Select(Normalize)
            .Where(name => !beforeNames.Contains(name)).Distinct().ToArray();
        // Chỉ bỏ bớt yêu cầu: lớp xác nhận thay đổi ràng buộc đã hỏi người dùng, không chặn ở đây.
        if (addedTags.Length == 0 && addedNames.Length == 0) return false;
        var accented = content.Normalize(NormalizationForm.FormC).ToLowerInvariant();
        return !addedTags.Any(id => tagNames.TryGetValue(id, out var name) && TagMentioned(name, accented)) &&
            !addedNames.Any(name => NameMentioned(name, text));
    }

    /// <summary>
    /// Removes the dish/restaurant names the turn added to meal requests, keeping their tags; used when the turn ends on a catalog
    /// question, because a name the catalog did not settle is not a user constraint and picking another must need no confirmation.
    /// </summary>
    public static AgentState WithoutUnsettledNames(AgentState state, AgentState baseline)
    {
        var requests = new List<AgentMealRequest>();
        foreach (var request in state.MealRequests)
        {
            if (request.DishName is null && request.RestaurantName is null || baseline.MealRequests.Any(previous => SameNames(previous, request)))
            { requests.Add(request); continue; }
            var unnamed = request with { DishName = null, RestaurantName = null, RestaurantId = null };
            if (unnamed.TagIds.Length > 0 || unnamed.DifferentFromPlan) requests.Add(unnamed);
        }
        return state with { MealRequests = [.. requests] };
    }

    private static bool SameNames(AgentMealRequest previous, AgentMealRequest current) => previous.DayNumber == current.DayNumber &&
        previous.MealSlot == current.MealSlot && previous.DishName == current.DishName && previous.RestaurantName == current.RestaurantName &&
        previous.RestaurantId == current.RestaurantId;

    private static bool TagMentioned(string tagName, string accentedContent) =>
        Regex.Split(tagName.Normalize(NormalizationForm.FormC).ToLowerInvariant(), @"[\s(),]+")
            .Where(word => word.Length > 1 && !GenericTagWords.Contains(word))
            .Any(word => Regex.IsMatch(accentedContent, @"(?<!\p{L})" + Regex.Escape(word) + @"(?!\p{L})", RegexOptions.CultureInvariant));

    // Tên món/quán coi là đã nhắc khi có nguyên tên, hoặc mọi chữ có nghĩa của tên đều xuất hiện trong tin nhắn.
    private static bool NameMentioned(string name, string text) => text.Contains(name, StringComparison.Ordinal) ||
        name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(word => word.Length > 1)
            .All(word => Regex.IsMatch(text, @"\b" + Regex.Escape(word) + @"\b", RegexOptions.CultureInvariant));

    // Chỉ so ràng buộc người dùng đặt: hồ sơ khẩu vị được backend khởi tạo khi xếp hạng, ghi chú/tiêu đề do model viết
    // và các trường do backend quản lý không phải là yêu cầu thay đổi của người dùng.
    private static bool SameUserConstraints(AgentState before, AgentState after) => AgentConversationRules.SameConstraints(Strip(before), Strip(after));

    private static AgentState Strip(AgentState state) => state with
    {
        Note = null, Title = "", Preferences = null, PendingPlanningRequest = null, PendingStateChange = null,
        SourceRoadmapUpdatedAt = null, HistoryStartSequence = null
    };

    private static string Normalize(string text) => new string(text.Normalize(NormalizationForm.FormD)
        .Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark).ToArray())
        .Normalize(NormalizationForm.FormC).ToLowerInvariant().Replace('đ', 'd');
}
