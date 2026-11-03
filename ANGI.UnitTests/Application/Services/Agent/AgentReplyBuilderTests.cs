using System.Text.Json;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Services.Agent;
using FluentAssertions;

namespace ANGI.UnitTests.Application.Services.Agent;

/// <summary>Checks honest user-facing taste guidance and fallback warnings tied to the selected items.</summary>
public sealed class AgentReplyBuilderTests
{
    // TEST-01: The saved itinerary lists every dish by day and meal, including several dishes per meal.
    /// <summary>Verifies a user who asked to see a saved plan gets all of it, since no proposal card shows it.</summary>
    [Fact]
    public void Itinerary_ListsEverySelectedDishByDayAndMealWithVariableCounts()
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

        var reply = AgentReplyBuilder.Itinerary(AgentTestData.Snapshot(items.Reverse().ToArray()));

        reply.Should().StartWith("Đây là lịch 2 ngày đã lưu của bạn nhé.\n\nNgày 1:\n\n" +
            "Bữa sáng:\n- Món 1 — Quán A, 50.000 đồng.\n\n" +
            "Bữa trưa:\n- Món 2 — Quán A, 50.000 đồng.\n- Món 3 — Quán A, 50.000 đồng.\n\n" +
            "Bữa tối:\n- Món 4 — Quán A, 50.000 đồng.");
        reply.Should().MatchRegex("Ngày 2:\n\nBữa sáng:\n- Món 5[^\n]+\n\nBữa trưa:\n- Món 6[^\n]+\n\n" +
            "Bữa tối:\n- Món 7[^\n]+\n- Món 8[^\n]+\n- Món 9[^\n]+\n- Món 10[^\n]+");
        reply.Split('\n').Count(line => line.StartsWith("- ")).Should().Be(items.Length);
        reply.Should().Contain("Tổng tiền dự kiến: 500.000 đồng/người.");
        reply.Should().NotContain("Bữa phụ:").And.NotContain("Bữa khuya:").And.NotContain("roadmap");
    }

    // TEST-02: Empty meals and days stay visible in the saved itinerary with an explicit placeholder and no invented dish.
    /// <summary>Keeps missing meals honest in the user-facing itinerary.</summary>
    [Fact]
    public void Itinerary_EmptyMealsAndDaysRemainVisibleWithoutInventedDishes()
    {
        var item = AgentTestData.Item() with { DishName = "Mì Quảng" };

        var reply = AgentReplyBuilder.Itinerary(AgentTestData.Snapshot(item));

        reply.Should().Contain("Bữa sáng:\nChưa có món trong bữa này.");
        reply.Should().Contain("Ngày 2:\n\nBữa sáng:\nChưa có món trong bữa này.\n\n" +
            "Bữa trưa:\nChưa có món trong bữa này.\n\nBữa tối:\nChưa có món trong bữa này.");
        reply.Split('\n').Where(line => line.StartsWith("- ")).Should().ContainSingle().Which.Should().StartWith("- Mì Quảng");
    }

    // TEST-03: Snack and late-night meals appear in the saved itinerary when selected or declared, even if empty.
    /// <summary>Shows optional meals only when the plan actually includes them.</summary>
    [Fact]
    public void Itinerary_AdditionalMealsAreShownWhenSelectedOrRequested()
    {
        var snapshot = AgentTestData.Snapshot(AgentTestData.Item(meal: "snack") with { DishName = "Chè" });
        snapshot = snapshot with { State = snapshot.State with { MealSlots = ["snack", "late_night"] } };

        var reply = AgentReplyBuilder.Itinerary(snapshot);

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
        reply.Should().NotContain(AgentReplyBuilder.RatingFallbackNote);
        reply.Should().Contain(AgentReplyBuilder.TasteNote);
    }

    // TEST-06: A selected item without personalized provenance is disclosed as rating-ordered.
    /// <summary>Keeps rating fallback visible to the user.</summary>
    [Fact]
    public void SelectedFallbackItem_IsStillDisclosed()
    {
        var snapshot = AgentTestData.Snapshot(AgentTestData.Item());
        AgentReplyBuilder.Proposal(snapshot).Should().Contain(AgentReplyBuilder.RatingFallbackNote);
    }

    // TEST-07: A meal-level taste override also states that taste is a soft, unguaranteed preference.
    /// <summary>Applies the taste disclaimer to scoped overrides, not only trip targets.</summary>
    [Fact]
    public void MealTasteOverride_AlsoDisclosesThatTasteIsNotGuaranteed()
    {
        var snapshot = AgentTestData.Snapshot(AgentTestData.Item());
        snapshot = snapshot with { State = snapshot.State with { TasteOverrides = [new(1, "lunch", [new("spicy", .5)])] } };
        AgentReplyBuilder.Proposal(snapshot).Should().Contain(AgentReplyBuilder.TasteNote);
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

    // TEST-11: The trip question names exactly the missing required values, skips the province when asked to, and is null when nothing is missing;
    // covers nothing known, only the budget missing, an unrestricted budget and a province-free check.
    /// <summary>Verifies the fixed question shared by clarifications, the recommendation tool and the taste question.</summary>
    [Theory]
    [InlineData("none", true, AgentIntakePolicy.PlaceQuestion)]
    [InlineData("budget", true, "Bạn cho mình biết ngân sách cho một người (tổng chuyến hoặc mỗi ngày) để lập lịch nhé?")]
    [InlineData("unrestricted", true, null)]
    [InlineData("none", false, "Bạn cho mình biết số ngày, ngân sách cho một người (tổng chuyến hoặc mỗi ngày) để lập lịch nhé?")]
    public void TripDetailsQuestion_NamesOnlyMissingValues(string known, bool askProvince, string? expected)
    {
        var state = known switch
        {
            "budget" => AgentTestData.State() with { BudgetAmount = null },
            "unrestricted" => AgentTestData.State() with { BudgetAmount = null, BudgetUnrestricted = true },
            _ => new AgentState()
        };

        AgentReplyBuilder.TripDetailsQuestion(state, askProvince).Should().Be(expected);
    }

    // TEST-12: The proposal reply states each applied taste with its level and scope, ordered trip → day → meal; covers trip, day and meal scopes.
    /// <summary>Lets the user see at once whether a taste was applied to the meal they named or widened to another scope.</summary>
    [Fact]
    public void Proposal_AppliedTastes_ListLevelAndScope()
    {
        var state = AgentTestData.State() with
        {
            TasteTargets = [new("spicy", .25)],
            TasteOverrides = [new(3, null, [new("sweet", .75)]), new(1, "lunch", [new("sour", .5)])]
        };

        var reply = AgentReplyBuilder.Proposal(new AgentSnapshot(state, [AgentTestData.Item()], []));

        reply.Should().Contain("Khẩu vị áp dụng: cay mức nhẹ cho toàn chuyến; chua mức vừa cho bữa trưa ngày 1; ngọt mức nhiều cho ngày 3.");
    }

    // TEST-13: Model-chosen taste questions name the tastes in the user's message and use the fixed taste scale; covers intensity with a taste,
    // intensity without a recognizable taste, and scope with a taste.
    /// <summary>Replaces the generic "thấp, hơi thấp…" question with the same specific wording as the backend taste question.</summary>
    [Theory]
    [InlineData("taste_intensity", "Hà Nội 3 ngày, cho tôi món hợp khẩu vị, đừng cay quá", "Bạn muốn mức cay như thế nào: không, nhẹ, vừa, nhiều hay rất nhiều?")]
    [InlineData("taste_intensity", "Cho tôi món hợp khẩu vị", "Bạn muốn vị nào và ở mức nào: không, nhẹ, vừa, nhiều hay rất nhiều?")]
    [InlineData("taste_scope", "Ít ngọt thôi", "Bạn muốn áp dụng khẩu vị ngọt cho toàn chuyến hay ngày/bữa cụ thể nào? Bạn muốn cập nhật các bữa đã có hay chỉ các gợi ý tiếp theo?")]
    public void Clarify_TasteQuestions_NameTheTasteAndShareTheScale(string kind, string content, string expected)
    {
        var context = AgentTestData.Context();
        context.Request.Content = content;

        AgentReplyBuilder.Clarify(context, kind).Should().Be(expected);
    }

    // TEST-14: A bare save intent is answered with the plan size, total and exact save wording, a saved-plan notice, or a no-preview notice;
    // covers ready, committed and missing proposals.
    /// <summary>Verifies "lưu" never saves or replans, but tells the user what would be saved and how to save it.</summary>
    [Theory]
    [InlineData("ready", "Mình lưu lịch hiện tại (2 món, tổng 80.000 đồng/người) nhé?")]
    [InlineData("committed", "Lịch này đã được lưu rồi. Bạn muốn chỉnh gì thêm không?")]
    [InlineData(null, AgentReplyBuilder.NothingToSave)]
    public void SaveConfirmation_DescribesWhatWouldBeSaved(string? status, string expected)
    {
        var snapshot = AgentTestData.Snapshot(AgentTestData.Item(1, "a") with { Price = 30000 }, AgentTestData.Item(2, "b", 2) with { Price = 50000 });
        var proposal = status is null ? null : new ANGI.Application.DTOs.Agent.AgentProposalDto(1, 1, "create", status, snapshot,
            new AgentValidation([], [], 80000, []), DateTime.UtcNow.AddHours(1));

        AgentReplyBuilder.SaveConfirmation(proposal).Should().Be(expected);
    }

    // TEST-15: Catalog questions repeat the real lookup result (missing name, near matches) when one exists, even after a later ranking
    // reset the tool notice; covers catalog_choice and catalog_support with and without a lookup result.
    /// <summary>Prevents "choose from the results found" without any results, or "not supported" without naming what was not found.</summary>
    [Theory]
    [InlineData("catalog_choice", true)][InlineData("catalog_choice", false)]
    [InlineData("catalog_support", true)][InlineData("catalog_support", false)]
    public void Clarify_CatalogQuestion_UsesLookupResult(string kind, bool looked)
    {
        const string lookup = "Chưa tìm thấy món/quán theo tên yêu cầu trong danh mục ANGI. Bạn có muốn xem lựa chọn khác không?\nPhố Cổ Quán — 1 Hàng Bạc, Hà Nội; khả dụng";
        var context = AgentTestData.Context();
        context.ToolNotice = null;
        if (looked) context.CatalogNotice = lookup;

        var reply = AgentReplyBuilder.Clarify(context, kind);

        if (looked) reply.Should().Be(lookup);
        else reply.Should().NotContain("Phố Cổ Quán").And.NotBeNullOrWhiteSpace();
    }

    // TEST-16: An exact catalog match answers a direct catalog question but never replaces a clarification; covers answer and edit_scope.
    /// <summary>Keeps the found-lookup text out of questions such as "Bạn muốn thay đổi toàn lịch hay ngày/bữa…".</summary>
    [Fact]
    public void CatalogAnswer_AnswersOnlyDirectQuestions()
    {
        var context = AgentTestData.Context();
        context.CatalogAnswer = "Đã tìm thấy kết quả đúng tên trong catalog.\nLẩu Bò Mai — Lẩu Bò Mai, 2 Tống Duy Tân, Hà Nội; khả dụng";

        AgentReplyBuilder.Answer(context).Should().Be(context.CatalogAnswer);
        AgentReplyBuilder.Clarify(context, "edit_scope").Should().StartWith("Bạn muốn đổi món nào, hay cả lịch?");
    }

    // TEST-17: A preview reply is a short introduction naming the place, days and total; it lists no dish, restaurant or internal wording.
    /// <summary>Stops the chat from repeating the proposal card, which caused two copies of the same plan on screen.</summary>
    [Fact]
    public void Proposal_IsShortIntroductionWithoutRepeatingTheCard()
    {
        var items = new[] { AgentTestData.Item(1, "a", 1, "breakfast"), AgentTestData.Item(2, "b", 1, "lunch"), AgentTestData.Item(3, "c", 1, "dinner"),
                AgentTestData.Item(4, "d", 2, "breakfast"), AgentTestData.Item(5, "e", 2, "lunch"), AgentTestData.Item(6, "f", 2, "dinner") }
            .Select(item => item with { DishName = $"Món {item.DishId}", RestaurantName = "Quán A", Price = 50000, RequestId = Guid.NewGuid() }).ToArray();

        var reply = AgentReplyBuilder.Proposal(AgentTestData.Snapshot(items));

        reply.Should().Be("Xong rồi! Mình đã lên lịch 2 ngày ở Da Nang cho bạn, tổng khoảng 300.000 đồng/người. Bạn xem danh sách món và các điểm trên bản đồ nhé.\n" +
            "Ưng rồi thì cứ bảo mình chốt, muốn đổi món nào cứ nhắn nhé.");
    }

    // TEST-18: A preview names its empty meals, omits the place when unknown, and caps the list at six plus a remainder; covers 5 and 8 empty meals.
    /// <summary>The card lists only existing dishes, so an empty meal must still be disclosed without turning the reply into a long list.</summary>
    [Theory]
    [InlineData(2, "Còn thiếu món cho bữa sáng ngày 1; bữa tối ngày 1; bữa sáng ngày 2; bữa trưa ngày 2; bữa tối ngày 2.")]
    [InlineData(3, "Còn thiếu món cho bữa sáng ngày 1; bữa tối ngày 1; bữa sáng ngày 2; bữa trưa ngày 2; bữa tối ngày 2; bữa sáng ngày 3; 2 bữa khác.")]
    public void Proposal_EmptyMeals_AreNamedAndCapped(int days, string expected)
    {
        var snapshot = AgentTestData.Snapshot(AgentTestData.Item() with { RequestId = Guid.NewGuid() });
        snapshot = snapshot with { State = snapshot.State with { NumDays = (short)days, Province = null } };

        var reply = AgentReplyBuilder.Proposal(snapshot);

        reply.Should().StartWith($"Xong rồi! Mình đã lên lịch {days} ngày cho bạn,").And.Contain(expected);
    }

    // TEST-19: Answer never dumps the plan for an unrelated question, and shows it only on request; covers another question, a view request over a
    // ready proposal, a view request over a saved plan and a view request with no plan.
    /// <summary>Fixes "xin chào" and any other question receiving the entire itinerary as the reply.</summary>
    [Theory]
    [InlineData("Giải thích bản nháp", "ready", AgentReplyBuilder.AnswerUnavailable)]
    [InlineData("Xem lại lịch giúp mình", "ready", "Lịch hiện tại có 1 món, tổng khoảng 50.000 đồng/người. Bạn xem chi tiết ở thẻ bên dưới và trên bản đồ nhé.")]
    [InlineData("Xem lại lịch giúp mình", "saved", "Đây là lịch 2 ngày đã lưu của bạn nhé.")]
    [InlineData("Xem lại lịch giúp mình", "none", "Mình có thể lập lịch ăn uống từ những món và quán có trên ANGI.")]
    public void Answer_ListsThePlanOnlyWhenAskedAndNoCardShowsIt(string content, string plan, string expected)
    {
        var snapshot = AgentTestData.Snapshot(AgentTestData.Item() with { DishName = "Mì Quảng", Price = 50000 });
        var context = new AgentContext
        {
            ConversationId = 7, UserId = 3, State = AgentTestData.State(), Source = plan == "none" ? null : snapshot,
            Request = new ANGI.Application.DTOs.Agent.SendAgentMessageRequestDto { ClientMessageId = 1, ExpectedConversationVersion = 1, Content = content },
            CurrentProposal = plan != "ready" ? null
                : new ANGI.Application.DTOs.Agent.AgentProposalDto(1, 1, "create", "ready", snapshot, new AgentValidation([], [], 50000, []), DateTime.UtcNow.AddHours(1))
        };

        var reply = AgentReplyBuilder.Answer(context);

        reply.Should().StartWith(expected);
        reply.Contains("Mì Quảng").Should().Be(plan == "saved");
    }

    // TEST-20: A tool notice still takes priority over every plan answer.
    /// <summary>Keeps trusted tool outcomes visible rather than replacing them with a generic reply.</summary>
    [Fact]
    public void Answer_ToolNotice_TakesPriority()
    {
        var context = AgentTestData.Context(AgentTestData.Snapshot(AgentTestData.Item()));
        context.ToolNotice = "Chưa lấy được gợi ý.";

        AgentReplyBuilder.Answer(context).Should().Be("Chưa lấy được gợi ý.");
    }
}
