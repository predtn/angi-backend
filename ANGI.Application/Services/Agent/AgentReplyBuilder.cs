using System.Globalization;
using ANGI.Application.Common.Models.Agent;

namespace ANGI.Application.Services.Agent;

/// <summary>Builds factual responses from backend snapshots and fixed question intents, never provider prose.</summary>
public static class AgentReplyBuilder
{
    public static readonly string[] Clarifications = ["trip_details", "taste_scope", "taste_intensity", "edit_scope",
        "novelty", "catalog_choice", "coverage", "catalog_support", "general"];

    /// <summary>Summarizes the reviewed snapshot, with a bounded list of actual catalog items.</summary>
    public static string Proposal(AgentSnapshot snapshot, AgentValidation? validation = null, bool saved = false)
    {
        var lines = new List<string> { $"{(saved ? "Lịch đã lưu" : "Bản đề xuất hiện tại")} có {snapshot.Items.Length} món trong {snapshot.State.NumDays} ngày." };
        if (snapshot.Changes is { Length: > 0 } changes)
            lines.Add($"Thay đổi: thêm {changes.Count(x => x.Type == "add")}, thay {changes.Count(x => x.Type == "replace")}, " +
                $"xóa {changes.Count(x => x.Type == "remove")}, chuyển {changes.Count(x => x.Type == "move")} món.");
        foreach (var item in snapshot.Items.Take(6))
            lines.Add($"Ngày {item.DayNumber}, {Meal(item.MealSlot)}: {Safe(item.DishName)} — {Safe(item.RestaurantName)}, {Money(item.Price)} đồng.");
        if (snapshot.Items.Length > 6) lines.Add("Các món còn lại có trong dữ liệu đề xuất đầy đủ.");
        lines.Add($"Tổng tiền dự kiến theo đề xuất: {Money(snapshot.Items.Sum(x => x.Price))} đồng/người.");
        if (snapshot.Items.Any(x => !x.RequestId.HasValue)) lines.Add("Một phần đề xuất dùng thứ tự đánh giá thay cho cá nhân hóa.");
        if (snapshot.State.TasteTargets.Length > 0 || snapshot.State.TasteOverrides.Length > 0)
            lines.Add("Khẩu vị là tiêu chí ưu tiên mềm, chưa bảo đảm từng món đạt mức vị đã chọn. Mức phù hợp còn phụ thuộc dữ liệu món và lịch sử tương tác.");
        if (validation?.Warnings.Any(x => x.Contains("ước lượng", StringComparison.Ordinal)) == true)
            lines.Add("Thông tin tuyến đường có phần ước lượng.");
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
            "coverage" => "Chưa đủ lựa chọn để hoàn thiện các bữa được yêu cầu. Bạn muốn điều chỉnh điều kiện hoặc số bữa nào?",
            "catalog_support" => "Yêu cầu này cần dữ liệu hoặc tag phù hợp trong catalog; mình chưa thể xác nhận từ dữ liệu hiện có. Bạn có muốn chọn tiêu chí khác không?",
            _ => "Bạn muốn lập lịch mới, xem lại đề xuất hay thay đổi ngày/bữa hoặc món nào?"
        };
    }

    /// <summary>Answers using the current snapshot or a trusted tool outcome, without inventing facts.</summary>
    public static string Answer(AgentContext context) => context.ToolNotice ?? (context.Source is { } source
        ? Proposal(source, saved: context.Roadmap is not null && context.CurrentProposal?.Status != "ready")
        : "Mình có thể lập lịch ăn uống từ những món và nhà hàng trong catalog ANGI. Bạn muốn lập lịch hoặc tìm món/quán nào?");

    private static string Meal(string value) => value switch
    { "breakfast" => "bữa sáng", "lunch" => "bữa trưa", "dinner" => "bữa tối", "snack" => "bữa phụ", "late_night" => "bữa khuya", _ => "bữa ăn" };
    private static string Money(decimal value) => value.ToString("N0", CultureInfo.GetCultureInfo("vi-VN"));
    private static string Safe(string? value) => value is null ? "Chưa có thông tin" : new string(value
        .Where(character => !char.IsControl(character) && character is not '<' and not '>').Take(200).ToArray());
}
