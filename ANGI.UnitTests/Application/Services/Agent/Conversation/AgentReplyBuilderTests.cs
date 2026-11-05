using System.Text.Json;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Services.Agent.Conversation;
using ANGI.Application.Services.Agent.Intake;
using FluentAssertions;

namespace ANGI.UnitTests.Application.Services.Agent.Conversation;

/// <summary>Checks honest user-facing taste guidance and fallback warnings tied to the selected items.</summary>
public sealed class AgentReplyBuilderTests
{
    // TEST-01: The saved itinerary lists every dish by day and meal, including several dishes per meal.
    /// <summary>Verifies a user who asked to see a saved plan gets all of it, since no proposal card shows it.</summary>
    [Fact]
    public void Itinerary_ListsEverySelectedDishByDayAndMealWithVariableCounts()
    {
        // STEP-01: Get items from Select().ToArray and get reply from AgentReplyBuilder.Itinerary.
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

        // STEP-02: Assert reply starts with the expected value, reply matches the expected pattern, reply.Split().Count(filtered) is items.Length and 2 more.
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
        // STEP-01: Derive item from a copy of AgentTestData.Item() and get reply from AgentReplyBuilder.Itinerary.
        var item = AgentTestData.Item() with { DishName = "Mì Quảng" };

        var reply = AgentReplyBuilder.Itinerary(AgentTestData.Snapshot(item));

        // STEP-02: Assert reply contains the expected text, reply contains the expected value, reply.Split().Where(filtered) has exactly one matching item and 1 more.
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
        // STEP-01: Get snapshot from AgentTestData.Snapshot, set snapshot to a copy of snapshot and get reply from AgentReplyBuilder.Itinerary.
        var snapshot = AgentTestData.Snapshot(AgentTestData.Item(meal: "snack") with { DishName = "Chè" });
        snapshot = snapshot with { State = snapshot.State with { MealSlots = ["snack", "late_night"] } };

        var reply = AgentReplyBuilder.Itinerary(snapshot);

        // STEP-02: Assert reply contains the expected text.
        reply.Should().Contain("Bữa phụ:\n- Chè").And.Contain("Bữa khuya:\nChưa có món trong bữa này.");
    }

    // TEST-04: A missing meal observed under an older taste is not reused as evidence after the taste changes.
    /// <summary>Ties coverage claims to the current effective constraints.</summary>
    [Fact]
    public void Coverage_ChangedTasteDoesNotReuseEarlierMissingMealAsEvidence()
    {
        // STEP-01: Get context from AgentTestData.Context, add the computed value to context.MealCoverage and set context.State to a copy of context.State.
        var context = AgentTestData.Context();
        context.MealCoverage.Add(new(1, "lunch", 0, AgentScopedPlanning.Fingerprint(context.State, 1, "lunch"), []));
        context.State = context.State with { TasteOverrides = [new(1, null, [new("sweet", .5)])] };

        // STEP-02: Assert AgentReplyBuilder.CoverageGaps() is empty and AgentReplyBuilder.Coverage() contains the expected text.
        AgentReplyBuilder.CoverageGaps(context).Should().BeEmpty();
        AgentReplyBuilder.Coverage(context).Should().Contain("chưa hoàn tất việc sắp xếp lịch").And.NotContain("chưa có lựa chọn phù hợp");
    }

    // TEST-05: Items with fresh personalized provenance do not inherit an earlier fallback warning, while taste guidance stays honest.
    /// <summary>Prevents stale fallback disclosures on personalized items.</summary>
    [Fact]
    public async Task FreshRecoItems_DoNotInheritAnOldFallbackWarning()
    {
        // STEP-01: Create requestId through Guid.NewGuid, derive state from a copy of AgentTestData.State(), derive item from a copy of AgentTestData.Item() and 2 more.
        var requestId = Guid.NewGuid();
        var state = AgentTestData.State() with { TasteTargets = [new("spicy", .75)] };
        var item = AgentTestData.Item() with { RequestId = requestId, Price = 50000 };
        var snapshot = new AgentSnapshot(state, [item],
            [new(null, null, [1], null, state), new(null, null, [1], requestId, state)]);
        var validator = AgentTestData.Validator(AgentTestData.Dish()).Validator;
        // STEP-02: Validate snapshot and keep validated.
        var validated = await validator.ValidateAsync(snapshot, null, true, false, default);
        // STEP-03: Assert validated.Validation.Errors is empty and validated.Validation.Warnings holds no matching item.
        validated.Validation.Errors.Should().BeEmpty();
        validated.Validation.Warnings.Should().NotContain(warning => warning.Contains("đánh giá"));
        // STEP-04: Get notes from AgentReplyBuilder.PlanNotes.
        var notes = AgentReplyBuilder.PlanNotes(snapshot);
        // STEP-05: Assert notes do not hold AgentReplyBuilder.RatingFallbackNote and one holds AgentReplyBuilder.TasteNote.
        notes.Should().NotContain(AgentReplyBuilder.RatingFallbackNote);
        notes.Should().Contain(note => note.Contains(AgentReplyBuilder.TasteNote));
    }

    // TEST-06: A selected item without personalized provenance is disclosed as rating-ordered.
    /// <summary>Keeps rating fallback visible to the user.</summary>
    [Fact]
    public void SelectedFallbackItem_IsStillDisclosed()
    {
        // STEP-01: Get snapshot from AgentTestData.Snapshot.
        var snapshot = AgentTestData.Snapshot(AgentTestData.Item());
        // STEP-02: Assert AgentReplyBuilder.PlanNotes() contains AgentReplyBuilder.RatingFallbackNote.
        AgentReplyBuilder.PlanNotes(snapshot).Should().Contain(AgentReplyBuilder.RatingFallbackNote);
    }

    // TEST-07: A meal-level taste override also states that taste is a soft, unguaranteed preference.
    /// <summary>Applies the taste disclaimer to scoped overrides, not only trip targets.</summary>
    [Fact]
    public void MealTasteOverride_AlsoDisclosesThatTasteIsNotGuaranteed()
    {
        // STEP-01: Get snapshot from AgentTestData.Snapshot and set snapshot to a copy of snapshot.
        var snapshot = AgentTestData.Snapshot(AgentTestData.Item());
        snapshot = snapshot with { State = snapshot.State with { TasteOverrides = [new(1, "lunch", [new("spicy", .5)])] } };
        // STEP-02: Assert AgentReplyBuilder.PlanNotes() holds AgentReplyBuilder.TasteNote.
        AgentReplyBuilder.PlanNotes(snapshot).Should().Contain(note => note.Contains(AgentReplyBuilder.TasteNote));
    }

    // TEST-08: The planning-incomplete reply asks the user to clarify or adjust instead of resending the same request.
    /// <summary>Avoids inviting an identical retry that would spend another paid LLM turn with the same outcome.</summary>
    [Fact]
    public void PlanningIncomplete_AsksForClarificationInsteadOfRetry()
    {
        // STEP-01: Assert AgentReplyBuilder.PlanningIncomplete contains the expected text.
        AgentReplyBuilder.PlanningIncomplete.Should().Contain("chưa hoàn tất việc sắp xếp lịch")
            .And.Contain("điều chỉnh yêu cầu").And.NotContain("thử lập lại").And.NotContain("thử lại");
    }

    // TEST-09: What to know about a plan goes on the card and the chat reply stays the sentence alone.
    /// <summary>A chat reply followed by a list of conditions is what read like a report.</summary>
    [Fact]
    public void Proposal_Conditions_StayOutOfTheChatReply()
    {
        // STEP-01: Prepare a plan with no budget and a taste.
        var plain = AgentTestData.Snapshot(AgentTestData.Item());
        var snapshot = plain with { State = plain.State with { BudgetUnrestricted = true, BudgetAmount = null, TasteTargets = [new("spicy", .25)] } };

        // STEP-02: Assert the reply is only the sentence and the notes hold the conditions.
        AgentReplyBuilder.Proposal(snapshot, wording: "Xong rồi nè.").Should().Be("Xong rồi nè.");
        AgentReplyBuilder.PlanNotes(snapshot).Should().Contain(AgentReplyBuilder.NoBudgetNote).And.Contain(note => note.Contains(AgentReplyBuilder.TasteNote));
    }

    // TEST-10: A general clarification only offers to review an existing plan; covers no plan before searches, no plan after searches, and an existing plan.
    /// <summary>Prevents the fallback question from inviting the user to review a proposal that was never built.</summary>
    [Theory]
    [InlineData("no-plan")][InlineData("searched")][InlineData("plan")]
    public void Clarify_GeneralQuestion_OffersReviewOnlyWhenAPlanExists(string situation)
    {
        // STEP-01: Get context from AgentTestData.Context, when situation == "searched", add the computed value to context.Exchanges and 1 more.
        var context = AgentTestData.Context(situation == "plan" ? AgentTestData.Snapshot(AgentTestData.Item()) : null);
        if (situation == "searched")
            context.Exchanges.Add(new([new("rank", "get_recommendation", JsonDocument.Parse("{}").RootElement)], [JsonDocument.Parse("{}").RootElement]));

        var reply = AgentReplyBuilder.Clarify(context, "general");

        // STEP-02: Assert depending on situation == "plan", reply contains the expected text, reply does not contain the expected text and 2 more.
        if (situation == "plan") reply.Should().Contain("xem lại lịch hiện tại");
        else reply.Should().NotContain("xem lại").And.NotContain("đề xuất");
        if (situation == "searched") reply.Should().Be(AgentReplyBuilder.PlanningIncomplete);
        if (situation == "no-plan") reply.Should().Contain("lập lịch ngay");
    }

    // TEST-11: The trip question names exactly the missing required values, asks the place first, and is null when nothing is missing;
    // covers nothing known, only the days and budget missing, only the budget missing and an unrestricted budget.
    /// <summary>Verifies the fixed question shared by clarifications and the recommendation tool.</summary>
    [Theory]
    [InlineData("none", AgentIntakePolicy.PlaceQuestion)]
    [InlineData("place", "Bạn cho mình biết số ngày, ngân sách cho một người (tổng chuyến hoặc mỗi ngày) để lập lịch nhé?")]
    [InlineData("budget", "Bạn cho mình biết ngân sách cho một người (tổng chuyến hoặc mỗi ngày) để lập lịch nhé?")]
    [InlineData("unrestricted", null)]
    public void TripDetailsQuestion_NamesOnlyMissingValues(string known, string? expected)
    {
        // STEP-01: Derive state from the expected value.
        var state = known switch
        {
            "place" => new AgentState { Province = "Hà Nội" },
            "budget" => AgentTestData.State() with { BudgetAmount = null },
            "unrestricted" => AgentTestData.State() with { BudgetAmount = null, BudgetUnrestricted = true },
            _ => new AgentState()
        };

        // STEP-02: Assert AgentReplyBuilder.TripDetailsQuestion() is expected.
        AgentReplyBuilder.TripDetailsQuestion(state).Should().Be(expected);
    }

    // TEST-12: The proposal reply states each applied taste with its level and scope, ordered trip → day → meal; covers trip, day and meal scopes.
    /// <summary>Lets the user see at once whether a taste was applied to the meal they named or widened to another scope.</summary>
    [Fact]
    public void Proposal_AppliedTastes_ListLevelAndScope()
    {
        // STEP-01: Derive state from a copy of AgentTestData.State() and get reply from AgentReplyBuilder.Proposal.
        var state = AgentTestData.State() with
        {
            TasteTargets = [new("spicy", .25)],
            TasteOverrides = [new(3, null, [new("sweet", .75)]), new(1, "lunch", [new("sour", .5)])]
        };

        var reply = string.Join(' ', AgentReplyBuilder.PlanNotes(new AgentSnapshot(state, [AgentTestData.Item()], [])));

        // STEP-02: Assert reply contains the expected text.
        reply.Should().Contain($"Mình ưu tiên khẩu vị cay mức nhẹ cho toàn chuyến; chua mức vừa cho bữa trưa ngày 1; ngọt mức nhiều cho ngày 3, {AgentReplyBuilder.TasteNote}.");
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
        // STEP-01: Get context from AgentTestData.Context and set context.Request.Content to content.
        var context = AgentTestData.Context();
        context.Request.Content = content;

        // STEP-02: Assert AgentReplyBuilder.Clarify() is expected.
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
        // STEP-01: Get snapshot from AgentTestData.Snapshot and derive proposal from the expected value.
        var snapshot = AgentTestData.Snapshot(AgentTestData.Item(1, "a") with { Price = 30000 }, AgentTestData.Item(2, "b", 2) with { Price = 50000 });
        var proposal = status is null ? null : new ANGI.Application.DTOs.Agent.AgentProposalDto(1, 1, "create", status, snapshot,
            new AgentValidation([], [], 80000, []), DateTime.UtcNow.AddHours(1));

        // STEP-02: Assert AgentReplyBuilder.SaveConfirmation() is expected.
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
        // STEP-01: Define lookup as the expected text, get context from AgentTestData.Context, set context.ToolNotice to null and 2 more.
        const string lookup = "Chưa tìm thấy món/quán theo tên yêu cầu trong danh mục ANGI. Bạn có muốn xem lựa chọn khác không?\nPhố Cổ Quán — 1 Hàng Bạc, Hà Nội; khả dụng";
        var context = AgentTestData.Context();
        context.ToolNotice = null;
        if (looked) context.CatalogNotice = lookup;

        var reply = AgentReplyBuilder.Clarify(context, kind);

        // STEP-02: Assert depending on looked, reply is lookup and reply does not contain the expected text.
        if (looked) reply.Should().Be(lookup);
        else reply.Should().NotContain("Phố Cổ Quán").And.NotBeNullOrWhiteSpace();
    }

    // TEST-16: An exact catalog match answers a direct catalog question but never replaces a clarification; covers answer and edit_scope.
    /// <summary>Keeps the found-lookup text out of questions such as "Bạn muốn thay đổi toàn lịch hay ngày/bữa…".</summary>
    [Fact]
    public void CatalogAnswer_AnswersOnlyDirectQuestions()
    {
        // STEP-01: Get context from AgentTestData.Context and set context.CatalogAnswer to a test text.
        var context = AgentTestData.Context();
        context.CatalogAnswer = "Đã tìm thấy kết quả đúng tên trong catalog.\nLẩu Bò Mai — Lẩu Bò Mai, 2 Tống Duy Tân, Hà Nội; khả dụng";

        // STEP-02: Assert AgentReplyBuilder.Answer() is context.CatalogAnswer and AgentReplyBuilder.Clarify() starts with the expected text.
        AgentReplyBuilder.Answer(context).Should().Be(context.CatalogAnswer);
        AgentReplyBuilder.Clarify(context, "edit_scope").Should().StartWith("Bạn muốn đổi món nào, hay cả lịch?");
    }

    // TEST-17: A preview reply is a short introduction naming the place, days and total; it lists no dish, restaurant or internal wording.
    /// <summary>Stops the chat from repeating the proposal card, which caused two copies of the same plan on screen.</summary>
    [Fact]
    public void Proposal_IsShortIntroductionWithoutRepeatingTheCard()
    {
        // STEP-01: Get items from Select().ToArray and get reply from AgentReplyBuilder.Proposal.
        var items = new[] { AgentTestData.Item(1, "a", 1, "breakfast"), AgentTestData.Item(2, "b", 1, "lunch"), AgentTestData.Item(3, "c", 1, "dinner"),
                AgentTestData.Item(4, "d", 2, "breakfast"), AgentTestData.Item(5, "e", 2, "lunch"), AgentTestData.Item(6, "f", 2, "dinner") }
            .Select(item => item with { DishName = $"Món {item.DishId}", RestaurantName = "Quán A", Price = 50000, RequestId = Guid.NewGuid() }).ToArray();

        var reply = AgentReplyBuilder.Proposal(AgentTestData.Snapshot(items));

        // STEP-02: Assert reply is the expected value.
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
        // STEP-01: Get snapshot from AgentTestData.Snapshot, set snapshot to a copy of snapshot and get reply from AgentReplyBuilder.Proposal.
        var snapshot = AgentTestData.Snapshot(AgentTestData.Item() with { RequestId = Guid.NewGuid() });
        snapshot = snapshot with { State = snapshot.State with { NumDays = (short)days, Province = null } };

        // STEP-02: Assert the reply starts with the expected text and the notes hold the empty meals.
        AgentReplyBuilder.Proposal(snapshot).Should().StartWith($"Xong rồi! Mình đã lên lịch {days} ngày cho bạn,");
        AgentReplyBuilder.PlanNotes(snapshot).Should().Contain(expected);
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
        // STEP-01: Get snapshot from AgentTestData.Snapshot, create context as AgentContext and get reply from AgentReplyBuilder.Answer.
        var snapshot = AgentTestData.Snapshot(AgentTestData.Item() with { DishName = "Mì Quảng", Price = 50000 });
        var context = new AgentContext
        {
            ConversationId = 7, UserId = 3, State = AgentTestData.State(), Source = plan == "none" ? null : snapshot,
            Request = new ANGI.Application.DTOs.Agent.SendAgentMessageRequestDto { ClientMessageId = 1, ExpectedConversationVersion = 1, Content = content },
            CurrentProposal = plan != "ready" ? null
                : new ANGI.Application.DTOs.Agent.AgentProposalDto(1, 1, "create", "ready", snapshot, new AgentValidation([], [], 50000, []), DateTime.UtcNow.AddHours(1))
        };

        var reply = AgentReplyBuilder.Answer(context);

        // STEP-02: Assert reply starts with expected and reply.Contains() is plan == "saved".
        reply.Should().StartWith(expected);
        reply.Contains("Mì Quảng").Should().Be(plan == "saved");
    }

    // TEST-20: A tool notice still takes priority over every plan answer.
    /// <summary>Keeps trusted tool outcomes visible rather than replacing them with a generic reply.</summary>
    [Fact]
    public void Answer_ToolNotice_TakesPriority()
    {
        // STEP-01: Get context from AgentTestData.Context and set context.ToolNotice to a test text.
        var context = AgentTestData.Context(AgentTestData.Snapshot(AgentTestData.Item()));
        context.ToolNotice = "Chưa lấy được gợi ý.";

        // STEP-02: Assert AgentReplyBuilder.Answer() is the expected text.
        AgentReplyBuilder.Answer(context).Should().Be("Chưa lấy được gợi ý.");
    }

    // TEST-21: A widened meal is stated with the place, both radii and its days, grouped per meal; a meal not in the plan is not mentioned, and a plan without widening says nothing.
    /// <summary>Ensures the user always learns that a meal was chosen further away, as the chosen behaviour promises.</summary>
    [Fact]
    public void Proposal_WidenedMeal_IsStated()
    {
        // STEP-01: Get plain from AgentTestData.Snapshot, derive state from a copy of plain.State and get reply from AgentReplyBuilder.Proposal.
        var plain = AgentTestData.Snapshot(AgentTestData.Item(1, "a", 1), AgentTestData.Item(2, "b", 2));
        var state = plain.State with { DayAreas = [new(1, "Hoàn Kiếm", 21m, 105.8m, 3m), new(2, "Hoàn Kiếm", 21m, 105.8m, 3m)],
            WidenedMeals = [new(1, "lunch", 8m), new(2, "lunch", 8m), new(1, "dinner", 12m)] };

        var reply = string.Join(' ', AgentReplyBuilder.PlanNotes(plain with { State = state }));

        // STEP-02: Assert reply contains the expected text, reply does not contain "12 km" and AgentReplyBuilder.Proposal() does not contain the expected text.
        reply.Should().Contain("Quanh Hoàn Kiếm trong 3 km chưa có món cho bữa trưa ngày 1, 2, nên bữa đó mình chọn quán trong khoảng 8 km.");
        reply.Should().NotContain("12 km");
        string.Join(' ', AgentReplyBuilder.PlanNotes(plain)).Should().NotContain("chưa có món cho");
    }

    // TEST-22: A preview names each itinerary day in order and how far a thin place reaches; a plan without an itinerary says nothing about it.
    /// <summary>Lets the user check at a glance that each day follows their itinerary.</summary>
    [Fact]
    public void Proposal_Itinerary_NamesEachDay()
    {
        // STEP-01: Get plain from AgentTestData.Snapshot and derive planned from a copy of plain.
        var plain = AgentTestData.Snapshot(AgentTestData.Item());
        var planned = plain with { State = plain.State with { DayAreas = [new(2, "Hòa Lạc", 21m, 105.5m, 30m), new(1, "Hoàn Kiếm", 21m, 105.8m, 3m)] } };

        // STEP-02: Assert AgentReplyBuilder.Proposal() does not contain the expected text and AgentReplyBuilder.Proposal() contains the expected text.
        string.Join(' ', AgentReplyBuilder.PlanNotes(plain)).Should().NotContain("Theo hành trình");
        string.Join(' ', AgentReplyBuilder.PlanNotes(planned)).Should().Contain("Theo hành trình của bạn: ngày 1 quanh Hoàn Kiếm; ngày 2 quanh Hòa Lạc (quanh đó còn ít quán").And.Contain("30 km");
    }

    // TEST-23: A preview with no budget says so; a preview with a budget does not.
    /// <summary>Stops the user from assuming a plan made without a budget was held to one.</summary>
    [Theory]
    [InlineData(true, null, true)][InlineData(false, 500000d, false)]
    public void Proposal_NoBudget_IsStated(bool unrestricted, double? budget, bool stated)
    {
        // STEP-01: Get plain from AgentTestData.Snapshot and derive snapshot from a copy of plain.
        var plain = AgentTestData.Snapshot(AgentTestData.Item());
        var snapshot = plain with { State = plain.State with { BudgetUnrestricted = unrestricted, BudgetAmount = (decimal?)budget } };

        // STEP-02: Assert AgentReplyBuilder.Proposal().Contains() is stated.
        AgentReplyBuilder.PlanNotes(snapshot).Contains(AgentReplyBuilder.NoBudgetNote).Should().Be(stated);
    }

    // TEST-24: The model's sentence about a new plan is filled with the final plan's facts; covers the total, the days and the place, and slots filled from the snapshot rather than the model.
    /// <summary>The model writes before the backend's repairs, so only values filled now can be true.</summary>
    [Fact]
    public void WordedSummary_NewPlan_FillsFinalFacts()
    {
        // STEP-01: Get snapshot from AgentTestData.Snapshot and context from AgentTestData.Context.
        var snapshot = AgentTestData.Snapshot(AgentTestData.Item() with { Price = 50000 });
        snapshot = snapshot with { State = snapshot.State with { Province = "Hà Nội", NumDays = 2 } };
        var context = AgentTestData.Context();

        // STEP-02: Prepare wording through AgentReplyBuilder.WordedSummary.
        var wording = AgentReplyBuilder.WordedSummary("Lịch {numDays} ngày ở {place} xong rồi nè, tổng khoảng {total}, bạn xem rồi chốt nhé!", snapshot, null, context);

        // STEP-03: Assert wording is the filled sentence.
        wording.Should().Be("Lịch 2 ngày ở Hà Nội xong rồi nè, tổng khoảng 50.000 đồng/người, bạn xem rồi chốt nhé!");
    }

    // TEST-25: A sentence about an edit names what really changed, including a swap the backend made and a removed dish; the rest of the plan is not named.
    /// <summary>The changes come from comparing the plans, so a dish swapped during repair is the one stated.</summary>
    [Fact]
    public void WordedSummary_Edit_StatesTheRealChanges()
    {
        // STEP-01: Create source with three dishes and snapshot keeping one, changing one and removing one.
        var source = AgentTestData.Snapshot(AgentTestData.Item(1, "a", 1, "lunch") with { DishName = "Bún Chả", Price = 30000 }, AgentTestData.Item(2, "b", 1, "dinner") with { DishName = "Mì Quảng" }, AgentTestData.Item(3, "c", 2, "lunch") with { DishName = "Cơm Tấm" });
        var snapshot = AgentTestData.Snapshot(AgentTestData.Item(1, "a", 1, "lunch") with { DishName = "Bún Chả", Price = 30000 }, AgentTestData.Item(4, "b", 1, "dinner") with { DishName = "Phở Gà", Price = 40000 });

        // STEP-02: Prepare wording through AgentReplyBuilder.WordedSummary.
        var wording = AgentReplyBuilder.WordedSummary("Mình đổi {changes} nhé, tổng giờ khoảng {total}.", snapshot, source, AgentTestData.Context());

        // STEP-03: Assert wording names the changes and the total, not the dish that stayed.
        wording.Should().Be("Mình đổi bữa tối ngày 1 sang Phở Gà; bỏ Cơm Tấm nhé, tổng giờ khoảng 70.000 đồng/người.");
    }

    // TEST-26: A sentence that invents something is refused; covers a new plan claiming changes, a typed price, a typed dish, a dish word, a change claim, an unknown slot and nothing.
    /// <summary>Refusing only loses the friendlier wording: the fixed summary still states the facts.</summary>
    [Theory]
    [InlineData("new", "Mình đổi {changes}, tổng {total} nhé.")]
    [InlineData("new", "Tổng {total}, tức khoảng 50.000 đồng nhé.")]
    [InlineData("new", "Có cả Bún Riêu nhé, tổng {total}.")]
    [InlineData("new", "Có cả phở nhé, tổng {total}.")]
    [InlineData("edit", "Mình đã đổi {changes}, tổng {total} nhé.")]
    [InlineData("new", "Tổng {total}, đánh giá {rating} nhé.")]
    [InlineData("new", null)]
    public void WordedSummary_MissingOrInvented_IsRefused(string kind, string? reply)
    {
        // STEP-01: Create snapshot and source from kind, and context with a dish word.
        var snapshot = AgentTestData.Snapshot(AgentTestData.Item() with { DishName = "Bún Chả" });
        var source = kind == "edit" ? AgentTestData.Snapshot(AgentTestData.Item(9, "z", 1, "lunch"), AgentTestData.Item() with { DishName = "Bún Chả" }) : null;
        var context = AgentTestData.Context();
        context.DishWords = new HashSet<string> { "phở" };

        // STEP-02: Assert AgentReplyBuilder.WordedSummary() is null.
        AgentReplyBuilder.WordedSummary(reply, snapshot, source, context).Should().BeNull();
    }

    // TEST-27: A refused sentence about a preview is reported with its reason; covers sentences that only leave a fact out, a new plan claiming changes, a word around the slots and a shown sentence.
    /// <summary>Tells a review whether the model ignored the slots or wrote something the checks refuse.</summary>
    [Theory]
    [InlineData("new", "Xong rồi nhé!", null)]
    [InlineData("new", "Lịch {numDays} ngày xong rồi nhé!", null)]
    [InlineData("edit", "Tổng giờ khoảng {total} nhé.", null)]
    [InlineData("new", "Mình đổi {changes}, tổng {total} nhé.", "unknown_slot")]
    [InlineData("edit", "Mình đã đổi {changes}, tổng {total} nhé.", "claim")]
    [InlineData("new", "Tổng {total}, tức 50.000 đồng nhé.", "number")]
    [InlineData("new", "Lịch xong rồi nè, tổng {total} nhé!", null)]
    // The two examples in the system prompt: a model copying one must not lose its wording to these checks.
    [InlineData("new", "Lịch {numDays} ngày ở {place} xong rồi nè, tổng khoảng {total}. Bạn xem thử, ưng thì bảo mình chốt nhé!", null)]
    [InlineData("edit", "Mình đổi {changes} nhé, tổng giờ khoảng {total}. Bạn xem lại, ổn thì mình chốt luôn nhé.", null)]
    public void WordedSummaryIssue_NamesTheReason(string kind, string reply, string? issue)
    {
        // STEP-01: Create snapshot and source from kind.
        var snapshot = AgentTestData.Snapshot(AgentTestData.Item() with { DishName = "Bún Chả" });
        var source = kind == "edit" ? AgentTestData.Snapshot(AgentTestData.Item(9, "z", 1, "lunch"), AgentTestData.Item() with { DishName = "Bún Chả" }) : null;

        // STEP-02: Assert AgentReplyBuilder.WordedSummaryIssue() is issue.
        AgentReplyBuilder.WordedSummaryIssue(reply, snapshot, source, AgentTestData.Context()).Should().Be(issue);
    }

    // TEST-28: A place slot needs a known place.
    /// <summary>Stops an empty slot from reaching the user.</summary>
    [Fact]
    public void WordedSummary_PlaceSlotWithoutPlace_IsRefused()
    {
        // STEP-01: Get snapshot from AgentTestData.Snapshot with no province.
        var snapshot = AgentTestData.Snapshot(AgentTestData.Item());
        snapshot = snapshot with { State = snapshot.State with { Province = null } };

        // STEP-02: Assert AgentReplyBuilder.WordedSummary() is null.
        AgentReplyBuilder.WordedSummary("Lịch ở {place} xong rồi, tổng {total} nhé.", snapshot, null, AgentTestData.Context()).Should().BeNull();
    }

    // TEST-29: The model's sentence replaces the fixed summary and closing; without it they are both shown.
    /// <summary>The fixed text is only the fallback for wording that failed its check.</summary>
    [Theory]
    [InlineData("Xong rồi nè, tổng {total}, bạn xem rồi chốt nhé!", false)]
    [InlineData(null, true)]
    public void Proposal_Wording_ReplacesSummary(string? wording, bool fixedText)
    {
        // STEP-01: Prepare reply through AgentReplyBuilder.Proposal.
        var reply = AgentReplyBuilder.Proposal(AgentTestData.Snapshot(AgentTestData.Item()), wording: wording);

        // STEP-02: Assert the fixed text follows fixedText.
        reply.Contains("Ưng rồi thì cứ bảo mình chốt").Should().Be(fixedText);
        reply.Contains("Mình đã lên lịch").Should().Be(fixedText);
    }

    // TEST-30: A sentence that leaves a fact out keeps the model's words and gains the fact; covers a new plan with no slot, an edit with no slot, no closing mark, a missing total and a complete sentence.
    /// <summary>Dropping the whole sentence for one missing slot is what sent the user the fixed text; the facts stay complete either way.</summary>
    [Theory]
    [InlineData("new", "Xong rồi nhé!", "Xong rồi nhé! Tổng khoảng 50.000 đồng/người.", "total")]
    [InlineData("edit", "Bạn xem lại nhé.", "Bạn xem lại nhé. Cụ thể: bữa tối ngày 1 sang Phở Gà; bỏ Cơm Tấm. Tổng giờ khoảng 70.000 đồng/người.", "changes,total")]
    [InlineData("edit", "Bạn xem lại nhé", "Bạn xem lại nhé. Cụ thể: bữa tối ngày 1 sang Phở Gà; bỏ Cơm Tấm. Tổng giờ khoảng 70.000 đồng/người.", "changes,total")]
    [InlineData("edit", "Mình đổi {changes} nhé.", "Mình đổi bữa tối ngày 1 sang Phở Gà; bỏ Cơm Tấm nhé. Tổng giờ khoảng 70.000 đồng/người.", "total")]
    [InlineData("edit", "Mình đổi {changes} nhé, tổng giờ khoảng {total}.", "Mình đổi bữa tối ngày 1 sang Phở Gà; bỏ Cơm Tấm nhé, tổng giờ khoảng 70.000 đồng/người.", "")]
    public void WordedSummary_MissingFact_IsAdded(string kind, string reply, string expected, string missing)
    {
        // STEP-01: Prepare the plan of a new trip or an edit.
        var source = AgentTestData.Snapshot(AgentTestData.Item(1, "a", 1, "lunch") with { DishName = "Bún Chả", Price = 30000 }, AgentTestData.Item(2, "b", 1, "dinner") with { DishName = "Mì Quảng" }, AgentTestData.Item(3, "c", 2, "lunch") with { DishName = "Cơm Tấm" });
        var edited = AgentTestData.Snapshot(AgentTestData.Item(1, "a", 1, "lunch") with { DishName = "Bún Chả", Price = 30000 }, AgentTestData.Item(4, "b", 1, "dinner") with { DishName = "Phở Gà", Price = 40000 });
        var snapshot = kind == "edit" ? edited : AgentTestData.Snapshot(AgentTestData.Item() with { Price = 50000 });
        var from = kind == "edit" ? source : null;

        // STEP-02: Assert the sentence and the slots reported missing.
        AgentReplyBuilder.WordedSummary(reply, snapshot, from, AgentTestData.Context()).Should().Be(expected);
        string.Join(',', AgentReplyBuilder.MissingSlots(reply, snapshot, from)).Should().Be(missing);
    }
}
