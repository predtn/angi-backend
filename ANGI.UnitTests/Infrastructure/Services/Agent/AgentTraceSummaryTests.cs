using System.Text.Json.Nodes;
using ANGI.Infrastructure.Services.Agent;
using FluentAssertions;

namespace ANGI.UnitTests.Infrastructure.Services.Agent;

/// <summary>Verifies the readable trace shows each candidate filter step, and lists a step that leaves no dish under VẤN ĐỀ.</summary>
public sealed class AgentTraceSummaryTests
{
    private static AgentTraceSummary.Event Step(double at, string name, object data) => new(at, name, JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(data)), null);

    // TEST-01: The SQL step states the hours rule and how many dishes it removed; the next step states the budget; reco states what it chose.
    /// <summary>Ensures a planned dinner reads "352 món → bỏ 18 vì giờ mở cửa → còn 334", then the budget per meal, then the ranking.</summary>
    [Fact]
    public void Build_PlanSteps_ReadsEachFilter()
    {
        var text = AgentTraceSummary.Build("turn", 1, "completed", 1000,
        [
            Step(10, "candidates_sql", new { source = "plan", day = 1, meal = "dinner", province = "Hà Nội", tags = new[] { "Món chính" }, mealTag = "dinner",
                hours = new { rule = "meal", meal = "dinner", from = "17:00", to = "21:00", weekdays = new[] { 0 } }, beforeHours = 352, afterHours = 334, found = 334 }),
            Step(20, "candidates_kept", new { source = "plan", day = 1, meal = "dinner", fromSql = 334, @checked = 300, distinct = 120, notReplaced = 120,
                priceRule = "budget", budgetPerMeal = 80000, kept = 60 }),
            Step(30, "ranking", new { mode = "personalized", candidateCount = 60, mealSlot = "dinner", returnedCount = 5 })
        ], "detail.log");

        text.Should().Contain("VẤN ĐỀ: không có");
        text.Should().Contain("[1] lọc cứng (SQL) · lập lịch ngày 1 bữa tối · giờ mở cửa (hours, rule=meal): quán mở trong bữa tối 17:00–21:00 (CN) · " +
            "352 món (beforeHours) → bỏ 18 vì giờ mở cửa → còn 334 (afterHours)");
        text.Should().Contain("[2] lọc tiếp · lập lịch ngày 1 bữa tối · từ SQL 334 (fromSql) · kiểm tag/bữa 300 (checked) · gộp chi nhánh cùng tên 120 (distinct) · " +
            "bỏ món đã đổi đi 120 (notReplaced) · ngân sách 80.000đ/bữa (priceRule=budget, budgetPerMeal) · → đưa reco 60 món (kept)");
        text.Should().Contain("bữa tối · reco xếp theo khẩu vị (mode=personalized) · 60 ứng viên (candidateCount) → chọn 5 (returnedCount)");
    }

    // TEST-02: A step that leaves nothing is a problem, and when business hours removed everything the line says so with every condition.
    /// <summary>Ensures "phở ngay bây giờ" at 22:36 that the hours empty is listed under VẤN ĐỀ with "giờ mở cửa loại hết".</summary>
    [Fact]
    public void Build_HoursRemoveEverything_ListedAsProblem()
    {
        var text = AgentTraceSummary.Build("turn", 1, "completed", 1000,
        [
            Step(10, "candidates_sql", new { source = "suggest", province = "Hà Nội", tags = Array.Empty<string>(),
                hours = new { rule = "now", from = "22:36", weekdays = new[] { 1 } }, beforeHours = 3, afterHours = 0, found = 0 })
        ], "detail.log");

        text.Should().Contain("VẤN ĐỀ (0 lỗi, 1 cảnh báo)");
        text.Should().Contain("giờ mở cửa (hours, rule=now): quán đang mở lúc 22:36 (T2)");
        text.Should().Contain("3 món (beforeHours) → bỏ 3 vì giờ mở cửa → còn 0 (afterHours) — giờ mở cửa loại hết");
        text.Should().Contain("tỉnh Hà Nội (province)").And.Contain("không lọc nhãn bữa (mealTag=null)");
    }
}
