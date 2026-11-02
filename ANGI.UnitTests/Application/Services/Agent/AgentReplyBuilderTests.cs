using System.Text.Json;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Services.Agent;
using FluentAssertions;

namespace ANGI.UnitTests.Application.Services.Agent;

/// <summary>Checks honest user-facing taste guidance and fallback warnings tied to the selected items.</summary>
public sealed class AgentReplyBuilderTests
{
    // TEST-01: The proposal reply lists every selected dish by day and meal, including several dishes per meal; covers preview and saved wording.
    /// <summary>Verifies the full itinerary is shown instead of a truncated summary.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Proposal_ListsEverySelectedDishByDayAndMealWithVariableCounts(bool saved)
    {
        var items = new[]
        {
            AgentTestData.Item(1, "a", 1, "breakfast"),
            AgentTestData.Item(2, "b", 1, "lunch"),
            AgentTestData.Item(3, "c", 1, "lunch") with { SortOrder = 1 },
            AgentTestData.Item(4, "d", 1, "dinner"),
            AgentTestData.Item(5, "e", 2, "breakfast"),
            AgentTestData.Item(6, "f", 2, "lunch"),
            AgentTestData.Item(7, "g", 2, "dinner"),
            AgentTestData.Item(8, "h", 2, "dinner") with { SortOrder = 1 },
            AgentTestData.Item(9, "i", 2, "dinner") with { SortOrder = 2 },
            AgentTestData.Item(10, "j", 2, "dinner") with { SortOrder = 3 }
        }.Select(item => item with { DishName = $"Món {item.DishId}", RestaurantName = "Quán A", Price = 50000,
            RequestId = Guid.NewGuid() }).ToArray();

        var reply = AgentReplyBuilder.Proposal(AgentTestData.Snapshot(items.Reverse().ToArray()), saved: saved);

        reply.Should().StartWith("Đây sẽ là roadmap khám phá ẩm thực của bạn trong 2 ngày.\n\nNgày 1:\n\n" +
            "Bữa sáng:\n- Món 1 — Quán A, 50.000 đồng.\n\n" +
            "Bữa trưa:\n- Món 2 — Quán A, 50.000 đồng.\n- Món 3 — Quán A, 50.000 đồng.\n\n" +
            "Bữa tối:\n- Món 4 — Quán A, 50.000 đồng.");
        reply.Should().MatchRegex("Ngày 2:\n\nBữa sáng:\n- Món 5[^\n]+\n\nBữa trưa:\n- Món 6[^\n]+\n\n" +
            "Bữa tối:\n- Món 7[^\n]+\n- Món 8[^\n]+\n- Món 9[^\n]+\n- Món 10[^\n]+");
        reply.Split('\n').Count(line => line.StartsWith("- ")).Should().Be(items.Length);
        reply.Should().Contain("Tổng tiền dự kiến theo đề xuất: 500.000 đồng/người.");
        reply.Should().Contain(saved ? "Đây là lịch đã lưu." : "Đây là bản xem trước.");
        reply.Should().NotContain("Các món còn lại").And.NotContain("Bữa phụ:").And.NotContain("Bữa khuya:");
    }

    // TEST-02: Empty meals and days stay visible with an explicit placeholder and no invented dish.
    /// <summary>Keeps missing meals honest in the user-facing itinerary.</summary>
    [Fact]
    public void Proposal_EmptyMealsAndDaysRemainVisibleWithoutInventedDishes()
    {
        var item = AgentTestData.Item() with { DishName = "Mì Quảng" };

        var reply = AgentReplyBuilder.Proposal(AgentTestData.Snapshot(item));

        reply.Should().Contain("Bữa sáng:\nChưa có món trong bữa này.");
        reply.Should().Contain("Ngày 2:\n\nBữa sáng:\nChưa có món trong bữa này.\n\n" +
            "Bữa trưa:\nChưa có món trong bữa này.\n\nBữa tối:\nChưa có món trong bữa này.");
        reply.Split('\n').Where(line => line.StartsWith("- ")).Should().ContainSingle().Which.Should().StartWith("- Mì Quảng");
    }

    // TEST-03: Snack and late-night meals appear when selected or declared, even if empty.
    /// <summary>Shows optional meals only when the plan actually includes them.</summary>
    [Fact]
    public void Proposal_AdditionalMealsAreShownWhenSelectedOrRequested()
    {
        var snapshot = AgentTestData.Snapshot(AgentTestData.Item(meal: "snack") with { DishName = "Chè" });
        snapshot = snapshot with { State = snapshot.State with { MealSlots = ["snack", "late_night"] } };

        var reply = AgentReplyBuilder.Proposal(snapshot);

        reply.Should().Contain("Bữa phụ:\n- Chè").And.Contain("Bữa khuya:\nChưa có món trong bữa này.");
    }

    // TEST-04: A missing meal observed under an older taste is not reused as evidence after the taste changes.
    /// <summary>Ties coverage claims to the current effective constraints.</summary>
    [Fact]
    public void Coverage_ChangedTasteDoesNotReuseEarlierMissingMealAsEvidence()
    {
        var context = AgentTestData.Context();
        context.MealCoverage.Add(new(1, "lunch", 0, AgentScopedPlanning.Fingerprint(context.State, 1, "lunch"), []));
        context.State = context.State with { TasteOverrides = [new(1, null, [new("sweet", .5)])] };

        AgentReplyBuilder.CoverageGaps(context).Should().BeEmpty();
        AgentReplyBuilder.Coverage(context).Should().Contain("chưa hoàn tất việc sắp xếp lịch").And.NotContain("chưa có lựa chọn phù hợp");
    }

    // TEST-05: Items with fresh personalized provenance do not inherit an earlier fallback warning, while taste guidance stays honest.
    /// <summary>Prevents stale fallback disclosures on personalized items.</summary>
    [Fact]
    public async Task FreshRecoItems_DoNotInheritAnOldFallbackWarning()
    {
        var requestId = Guid.NewGuid();
        var state = AgentTestData.State() with { TasteTargets = [new("spicy", .75)] };
        var item = AgentTestData.Item() with { RequestId = requestId, Price = 50000 };
        var snapshot = new AgentSnapshot(state, [item],
            [new(null, null, [1], null, state), new(null, null, [1], requestId, state)]);
        var validator = AgentTestData.Validator(AgentTestData.Dish()).Validator;
        var validated = await validator.ValidateAsync(snapshot, null, true, false, default);
        validated.Validation.Errors.Should().BeEmpty();
        validated.Validation.Warnings.Should().NotContain(warning => warning.Contains("đánh giá"));
        var reply = AgentReplyBuilder.Proposal(snapshot);
        reply.Should().NotContain("thứ tự đánh giá");
        reply.Should().Contain("ưu tiên mềm").And.Contain("chưa bảo đảm từng món").And.Contain("lịch sử tương tác");
    }

    // TEST-06: A selected item without personalized provenance is disclosed as rating-ordered.
    /// <summary>Keeps rating fallback visible to the user.</summary>
    [Fact]
    public void SelectedFallbackItem_IsStillDisclosed()
    {
        var snapshot = AgentTestData.Snapshot(AgentTestData.Item());
        AgentReplyBuilder.Proposal(snapshot).Should().Contain("thứ tự đánh giá thay cho cá nhân hóa");
    }

    // TEST-07: A meal-level taste override also states that taste is a soft, unguaranteed preference.
    /// <summary>Applies the taste disclaimer to scoped overrides, not only trip targets.</summary>
    [Fact]
    public void MealTasteOverride_AlsoDisclosesThatTasteIsNotGuaranteed()
    {
        var snapshot = AgentTestData.Snapshot(AgentTestData.Item());
        snapshot = snapshot with { State = snapshot.State with { TasteOverrides = [new(1, "lunch", [new("spicy", .5)])] } };
        AgentReplyBuilder.Proposal(snapshot).Should().Contain("chưa bảo đảm từng món");
    }

    // TEST-08: The planning-incomplete reply asks the user to clarify or adjust instead of resending the same request.
    /// <summary>Avoids inviting an identical retry that would spend another paid LLM turn with the same outcome.</summary>
    [Fact]
    public void PlanningIncomplete_AsksForClarificationInsteadOfRetry()
    {
        AgentReplyBuilder.PlanningIncomplete.Should().Contain("chưa hoàn tất việc sắp xếp lịch")
            .And.Contain("điều chỉnh yêu cầu").And.NotContain("thử lập lại").And.NotContain("thử lại");
    }

    // TEST-09: Backend-substituted dishes are disclosed with their count and omitted when nothing was substituted; covers 0, 1 and 2.
    /// <summary>Keeps automatic substitution transparent to the user before they confirm the preview.</summary>
    [Theory]
    [InlineData(0)][InlineData(1)][InlineData(2)]
    public void Proposal_SubstitutedDishes_AreDisclosed(int substituted)
    {
        var reply = AgentReplyBuilder.Proposal(AgentTestData.Snapshot(AgentTestData.Item()), substituted: substituted);

        if (substituted == 0) reply.Should().NotContain(AgentReplyBuilder.SubstitutionNote);
        else reply.Should().Contain($"Có {substituted} món được hệ thống chọn lại").And.Contain("khẩu vị, ngân sách và điều kiện");
    }

    // TEST-10: A general clarification only offers to review an existing plan; covers no plan before searches, no plan after searches, and an existing plan.
    /// <summary>Prevents the fallback question from inviting the user to review a proposal that was never built.</summary>
    [Theory]
    [InlineData("no-plan")][InlineData("searched")][InlineData("plan")]
    public void Clarify_GeneralQuestion_OffersReviewOnlyWhenAPlanExists(string situation)
    {
        var context = AgentTestData.Context(situation == "plan" ? AgentTestData.Snapshot(AgentTestData.Item()) : null);
        if (situation == "searched")
            context.Exchanges.Add(new([new("rank", "get_recommendation", JsonDocument.Parse("{}").RootElement)], [JsonDocument.Parse("{}").RootElement]));

        var reply = AgentReplyBuilder.Clarify(context, "general");

        if (situation == "plan") reply.Should().Contain("xem lại lịch hiện tại");
        else reply.Should().NotContain("xem lại").And.NotContain("đề xuất");
        if (situation == "searched") reply.Should().Be(AgentReplyBuilder.PlanningIncomplete);
        if (situation == "no-plan") reply.Should().Contain("lập lịch ngay");
    }

    // TEST-14: A bare save intent is answered with the plan size, total and exact save wording, a saved-plan notice, or a no-preview notice;
    // covers ready, committed and missing proposals.
    /// <summary>Verifies "lưu" never saves or replans, but tells the user what would be saved and how to save it.</summary>
    [Theory]
    [InlineData("ready", "Bạn muốn lưu bản xem trước hiện tại (2 món, tổng 80.000 đồng/người) thành roadmap? Nhắn ‘chốt lịch này’ để lưu.")]
    [InlineData("committed", "Lịch trình này đã được lưu thành roadmap. Bạn muốn chỉnh sửa gì thêm không?")]
    [InlineData(null, "Chưa có bản xem trước sẵn sàng để lưu. Hãy yêu cầu tạo lịch trước.")]
    public void SaveConfirmation_DescribesWhatWouldBeSaved(string? status, string expected)
    {
        var snapshot = AgentTestData.Snapshot(AgentTestData.Item(1, "a") with { Price = 30000 }, AgentTestData.Item(2, "b", 2) with { Price = 50000 });
        var proposal = status is null ? null : new ANGI.Application.DTOs.Agent.AgentProposalDto(1, 1, "create", status, snapshot,
            new AgentValidation([], [], 80000, []), DateTime.UtcNow.AddHours(1));

        AgentReplyBuilder.SaveConfirmation(proposal).Should().Be(expected);
    }
}
