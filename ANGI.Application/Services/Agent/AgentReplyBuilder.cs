using System.Globalization;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.UseCases.Validators.Agent;

namespace ANGI.Application.Services.Agent;

/// <summary>Builds factual responses from backend snapshots and fixed question intents, never provider prose.</summary>
public static class AgentReplyBuilder
{
    // Không mời gửi lại y nguyên: cùng dữ liệu và cùng yêu cầu thường lặp lại đúng kết quả nhưng vẫn tốn lượt gọi AI.
    public const string PlanningIncomplete = "Mình chưa hoàn tất việc sắp xếp lịch theo yêu cầu của bạn. " +
        "Bạn có thể nói rõ hơn hoặc điều chỉnh yêu cầu, ví dụ ngày/bữa, món muốn ăn, ngân sách hoặc khẩu vị.";
    public const string SubstitutionNote = "món được hệ thống chọn lại từ chính danh sách gợi ý đã lấy để đúng ngày/bữa hoặc tránh lặp món; " +
        "các món này vẫn theo khẩu vị, ngân sách và điều kiện bạn đã nêu.";
    public const string RepeatNote = "Một số món xuất hiện ở nhiều bữa vì catalog hiện chưa có đủ món khác nhau đáp ứng điều kiện của các bữa đó.";
    public static readonly string[] Clarifications = ["trip_details", "taste_scope", "taste_intensity", "edit_scope",
        "novelty", "catalog_choice", "coverage", "catalog_support", "general"];

    /// <summary>Lists the full reviewed itinerary by day and meal using actual catalog items.</summary>
    public static string Proposal(AgentSnapshot snapshot, AgentValidation? validation = null, bool saved = false, int substituted = 0)
    {
        var lines = new List<string> { $"Đây sẽ là roadmap khám phá ẩm thực của bạn trong {snapshot.State.NumDays} ngày." };
        lines.Add("");
        var itemsByMeal = AgentJson.OrderItems(snapshot.Items).ToLookup(item => (item.DayNumber, item.MealSlot));
        for (short day = 1; day <= snapshot.State.NumDays; day++)
        {
            lines.Add($"Ngày {day}:\n");
            foreach (var meal in AgentStateValidator.Meals)
            {
                var items = itemsByMeal[(day, meal)].ToArray();
                if (meal is "snack" or "late_night" && items.Length == 0 && !snapshot.State.MealSlots.Contains(meal)) continue;
                var label = Meal(meal);
                lines.Add($"{char.ToUpperInvariant(label[0])}{label[1..]}:");
                foreach (var item in items)
                    lines.Add($"- {Safe(item.DishName)} — {Safe(item.RestaurantName)}, {Money(item.Price)} đồng.");
                if (items.Length == 0) lines.Add("Chưa có món trong bữa này.");
                lines.Add("");
            }
        }
        lines.Add($"Tổng tiền dự kiến theo đề xuất: {Money(snapshot.Items.Sum(x => x.Price))} đồng/người.");
        if (snapshot.Items.Any(x => !x.RequestId.HasValue)) lines.Add("Một phần đề xuất dùng thứ tự đánh giá thay cho cá nhân hóa.");
        if (snapshot.State.TasteTargets.Length > 0 || snapshot.State.TasteOverrides.Length > 0)
            lines.Add("Khẩu vị là tiêu chí ưu tiên mềm, chưa bảo đảm từng món đạt mức vị đã chọn. Mức phù hợp còn phụ thuộc dữ liệu món và lịch sử tương tác.");
        if (validation?.Warnings.Any(x => x.Contains("ước lượng", StringComparison.Ordinal)) == true)
            lines.Add("Thông tin tuyến đường có phần ước lượng.");
        if (substituted > 0) lines.Add($"Có {substituted} {SubstitutionNote}");
        // Lặp còn lại sau khi backend đã thay các món lặp tránh được là do catalog; món người dùng gọi tên không tính.
        if (snapshot.Items.Where(item => AgentScopedPlanning.Request(snapshot.State, item.DayNumber, item.MealSlot) is { DishName: null, RestaurantName: null })
                .GroupBy(item => AgentScopedPlanning.Normalize(item.DishName)).Any(group => group.Key is not null && group.Count() > 1))
            lines.Add(RepeatNote);
        lines.Add(saved ? "Đây là lịch đã lưu. Các chỉnh sửa tiếp theo cần được xem lại và xác nhận lưu."
            : "Đây là bản xem trước. Chỉ lưu thành roadmap sau khi bạn xác nhận lưu.");
        return string.Join('\n', lines);
    }

    /// <summary>Asks only unresolved required values or the model's allowlisted clarification intent.</summary>
    public static string Clarify(AgentContext context, string? kind)
    {
        if (context.ToolNotice is { } notice) return notice;
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(context.State.Province)) missing.Add("tỉnh/thành phố");
        if (!context.State.NumDays.HasValue) missing.Add("số ngày");
        if (context.State.BudgetAmount is null && !context.State.BudgetUnrestricted) missing.Add("ngân sách cho một người");
        if (missing.Count > 0) return $"Bạn cho mình biết {string.Join(", ", missing)} để lập lịch nhé?";
        return kind switch
        {
            "taste_scope" => "Bạn muốn áp dụng khẩu vị này cho toàn chuyến hay ngày/bữa cụ thể? Bạn muốn cập nhật các bữa đã có hay chỉ các gợi ý tiếp theo?",
            "taste_intensity" => "Bạn muốn mức khẩu vị thấp, hơi thấp, vừa, hơi cao hay cao?",
            "edit_scope" => "Bạn muốn thay đổi toàn lịch hay ngày/bữa hoặc món cụ thể nào?",
            "novelty" => "Bạn muốn món khác các món trong lịch hiện tại, hay món bạn chưa từng ăn? ANGI chưa có đủ dữ liệu để xác nhận toàn bộ lịch sử ăn uống của bạn.",
            "catalog_choice" => "Bạn muốn chọn món hoặc nhà hàng nào trong các kết quả catalog đã tìm thấy?",
            "coverage" => Coverage(context),
            "catalog_support" => "Yêu cầu này cần dữ liệu hoặc tag phù hợp trong catalog; mình chưa thể xác nhận từ dữ liệu hiện có. Bạn có muốn chọn tiêu chí khác không?",
            // Chưa có lịch nào: không mời "xem lại đề xuất". Đã lấy gợi ý mà chưa ghép được lịch thì nói rõ là chưa hoàn tất.
            _ when context.Source is null => context.Exchanges.Count > 0 ? PlanningIncomplete
                : "Mình đã có địa điểm, số ngày và ngân sách. Bạn muốn mình lập lịch ngay hay bổ sung yêu cầu về bữa, món hoặc khẩu vị?",
            _ => "Bạn muốn xem lại lịch hiện tại hay thay đổi ngày/bữa hoặc món nào?"
        };
    }

    /// <summary>Only current, backend-observed empty meals support a claim of missing choices.</summary>
    public static AgentMealCoverage[] CoverageGaps(AgentContext context) => context.MealCoverage
        .Where(meal => meal.AvailableCount == 0 && meal.DayNumber <= context.State.NumDays &&
            (context.State.MealSlots.Contains(meal.MealSlot) || context.State.MealSlots.Length == 0) &&
            meal.Fingerprint == AgentScopedPlanning.Fingerprint(context.State, meal.DayNumber, meal.MealSlot))
        .OrderBy(meal => meal.DayNumber).ThenBy(meal => Array.IndexOf(UseCases.Validators.Agent.AgentStateValidator.Meals, meal.MealSlot)).ToArray();

    /// <summary>Names missing day/meals and known filters without claiming the whole catalog is exhausted.</summary>
    public static string Coverage(AgentContext context)
    {
        var gaps = CoverageGaps(context);
        if (gaps.Length == 0) return PlanningIncomplete;
        var details = gaps.Take(6).Select(meal => $"ngày {meal.DayNumber}, {Meal(meal.MealSlot)}" +
            (meal.RequiredTags.Length == 0 ? "" : $" (yêu cầu: {string.Join(", ", meal.RequiredTags.Select(Safe))})"));
        var remaining = gaps.Length > 6 ? $" và {gaps.Length - 6} bữa khác" : "";
        return $"Trong kết quả gợi ý vừa lấy, chưa có lựa chọn phù hợp cho {string.Join("; ", details)}{remaining}. " +
            "Mình chưa hoàn tất lịch mới. Bạn muốn mình tìm thêm cho các bữa này hay thay đổi yêu cầu của các bữa đó?";
    }

    /// <summary>Answers using the current snapshot or a trusted tool outcome, without inventing facts.</summary>
    public static string Answer(AgentContext context) => context.ToolNotice ?? (context.Source is { } source
        ? Proposal(source, saved: context.Roadmap is not null && context.CurrentProposal?.Status != "ready")
        : "Mình có thể lập lịch ăn uống từ những món và nhà hàng trong catalog ANGI. Bạn muốn lập lịch hoặc tìm món/quán nào?");

    internal static string Meal(string value) => value switch
    { "breakfast" => "bữa sáng", "lunch" => "bữa trưa", "dinner" => "bữa tối", "snack" => "bữa phụ", "late_night" => "bữa khuya", _ => "bữa ăn" };
    private static string Money(decimal value) => value.ToString("N0", CultureInfo.GetCultureInfo("vi-VN"));
    private static string Safe(string? value) => value is null ? "Chưa có thông tin" : new string(value
        .Where(character => !char.IsControl(character) && character is not '<' and not '>').Take(200).ToArray());
}
