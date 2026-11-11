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

    // TEST-06b: A dish kept from the saved roadmap was never ranked, so it does not count as a rating fallback.
    /// <summary>Stops every edit of a hand-made roadmap from claiming its dishes were ordered by rating (Round 2, AG-86).</summary>
    [Fact]
    public void KeptRoadmapItem_IsNotRatingFallback()
    {
        // STEP-01: Get snapshot holding one item kept from the roadmap.
        var snapshot = AgentTestData.Snapshot(AgentTestData.Item() with { SourceItemId = 41 });
        // STEP-02: Assert AgentReplyBuilder.PlanNotes() does not contain AgentReplyBuilder.RatingFallbackNote.
        AgentReplyBuilder.PlanNotes(snapshot).Should().NotContain(AgentReplyBuilder.RatingFallbackNote);
    }

    // TEST-06c: A new plan says no budget applies when no budget was set, whether or not the user said so; an edit of a saved roadmap only when they did.
    /// <summary>Covers a budget never discussed (Round 2, AG-10), an explicit "chưa chốt" and an edit of a roadmap that never had a budget.</summary>
    [Theory]
    [InlineData(false, null, true)]
    [InlineData(true, null, true)]
    [InlineData(false, 7L, false)]
    [InlineData(true, 7L, true)]
    public void NoBudget_IsNotedForNewPlansAndUndecidedBudgets(bool unrestricted, long? baseRoadmapId, bool noted)
    {
        // STEP-01: Get snapshot with no budget.
        var snapshot = AgentTestData.Snapshot(AgentTestData.Item()) with
            { State = AgentTestData.State() with { BudgetAmount = null, BudgetUnrestricted = unrestricted }, BaseRoadmapId = baseRoadmapId };
        // STEP-02: Assert whether AgentReplyBuilder.PlanNotes() contains AgentReplyBuilder.NoBudgetNote.
        AgentReplyBuilder.PlanNotes(snapshot).Contains(AgentReplyBuilder.NoBudgetNote).Should().Be(noted);
    }

    // TEST-06d: A draft made after the old one was dropped for an outside edit says so on the card; a deleted roadmap or a normal turn does not.
    /// <summary>The card states the outside edit even when the model's reply skips it (Round 2, AG-57).</summary>
    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    public void AttachNotes_OutsideEdit_IsStatedOnTheCard(bool dropped, bool deleted, bool noted)
    {
        // STEP-01: Create context with the flags.
        var context = new AgentContext
        {
            ConversationId = 7, UserId = 3, State = AgentTestData.State(), OutdatedProposalDropped = dropped, AttachedRoadmapDeleted = deleted,
            Request = new ANGI.Application.DTOs.Agent.SendAgentMessageRequestDto { Content = "Đổi bữa trưa ngày 1" }
        };
        // STEP-02: Attach notes to a snapshot and assert whether the first note is AgentReplyBuilder.OutsideEditNote.
        var notes = AgentReplyBuilder.AttachNotes(AgentTestData.Snapshot(AgentTestData.Item()) with { Notes = ["x"] }, context).Notes!;
        notes.Contains(AgentReplyBuilder.OutsideEditNote).Should().Be(noted);
        notes.Should().Contain("x");
    }

    // TEST-06f: A plan this turn made cost more than the budget gets the fixed over-budget question; covers a fitting plan, a reply that already
    // speaks of the budget, a plan already over the same budget, and a plan that went over because the budget was lowered.
    /// <summary>Fixes an over-budget plan being presented as fine when the model skipped the budget question (S1, AG-19).</summary>
    [Fact]
    public void WithBudgetNote_NewlyOverBudget_AddsQuestionOnce()
    {
        // STEP-01: Build a 370k plan, a source already over 300k and one that fit the older 500k budget.
        var plan = AgentTestData.Snapshot(AgentTestData.Item(1, "a") with { Price = 200000 }, AgentTestData.Item(2, "b", 1, "dinner") with { Price = 170000 });
        var overBefore = plan with { State = plan.State with { BudgetAmount = 300000 } };
        var fitBefore = plan with { State = plan.State with { BudgetAmount = 500000 } };
        const string reply = "Lịch xong rồi nè.";

        // STEP-02: Assert the note appears only when the plan newly went over.
        AgentReplyBuilder.WithBudgetNote(reply, plan, 400000, null).Should().Be(reply);
        AgentReplyBuilder.WithBudgetNote("Lịch vượt ngân sách nhé.", plan, 300000, null).Should().Be("Lịch vượt ngân sách nhé.");
        AgentReplyBuilder.WithBudgetNote(reply, plan, 300000, overBefore).Should().Be(reply);
        AgentReplyBuilder.WithBudgetNote("Mình cập nhật theo ngân sách mới nhé.", plan, 300000, null).Should().EndWith("hay giữ nguyên lịch này?");
        AgentReplyBuilder.WithBudgetNote(reply, plan, 300000, fitBefore).Should()
            .Be("Lịch xong rồi nè. Lịch này tổng khoảng 370.000 đồng/người, vượt ngân sách 300.000 đồng bạn đặt. Mình nâng ngân sách lên 370.000 đồng nhé; bạn có muốn mình chọn lại món cho hợp ngân sách không, hay giữ nguyên lịch này?");
    }

    // TEST-06e: A template that already names the meal before {changes} does not say it twice; a plain template is unchanged.
    /// <summary>Fixes "đổi bữa tối ngày 2 sang bữa tối ngày 2 sang …" (Round 3, AG-35/AG-57).</summary>
    [Theory]
    [InlineData("Mình đổi bữa tối ngày 2 sang {changes} nhé.")]
    [InlineData("Mình đổi {changes} nhé.")]
    public void FillSlots_MealBeforeChanges_IsNotRepeated(string template)
    {
        // STEP-01: Build a source and a snapshot where only day 2 dinner changed.
        var kept = AgentTestData.Item(1, "a", 1, "lunch") with { DishName = "Phở" };
        var before = AgentTestData.Item(2, "b", 2, "dinner") with { DishName = "Bún Chả" };
        var source = AgentTestData.Snapshot(kept, before);
        var snapshot = AgentTestData.Snapshot(kept, before with { DishId = 3, DishName = "Bánh Pie Gà Nấm" });

        // STEP-02: Assert the meal is named once.
        AgentReplyBuilder.FillSlots(template, snapshot, source).Should().Be("Mình đổi bữa tối ngày 2 sang Bánh Pie Gà Nấm nhé.");
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

    // TEST-10: The trip question names exactly the missing required values, asks the place first, and is null when nothing is missing;
    // covers nothing known, only the days and budget missing, only the budget missing and an unrestricted budget.
    /// <summary>Verifies the fixed question shared by clarifications and the recommendation tool.</summary>
    [Theory]
    [InlineData("none", AgentIntakePolicy.PlaceQuestion)]
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

    // TEST-11: The proposal reply states each applied taste with its level and scope, ordered trip → day → meal; covers trip, day and meal scopes.
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

    // TEST-12: A preview reply is a short introduction naming the place, days and total; it lists no dish, restaurant or internal wording.
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

    // TEST-13: A preview names its empty meals, omits the place when unknown, and caps the list at six plus a remainder; covers 5 and 8 empty meals.
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

    // TEST-14: Answer never dumps the plan for an unrelated question, and shows it only when the model read a wish to see it; covers another question, a view request over a
    // ready proposal, a view request over a saved plan and a view request with no plan.
    /// <summary>Fixes "xin chào" and any other question receiving the entire itinerary as the reply; the model, not a regex on the user's words, decides what was asked.</summary>
    [Theory]
    [InlineData(false, "ready", AgentReplyBuilder.AnswerUnavailable)]
    [InlineData(true, "ready", "Lịch hiện tại có 1 món, tổng khoảng 50.000 đồng/người. Bạn xem chi tiết ở thẻ bên dưới và trên bản đồ nhé.")]
    [InlineData(true, "saved", "Đây là lịch 2 ngày đã lưu của bạn nhé.")]
    [InlineData(true, "none", "Mình có thể lập lịch ăn uống từ những món và quán có trên ANGI.")]
    public void Answer_ListsThePlanOnlyWhenAskedAndNoCardShowsIt(bool planAsked, string plan, string expected)
    {
        // STEP-01: Get snapshot from AgentTestData.Snapshot, create context as AgentContext and get reply from AgentReplyBuilder.Answer.
        var snapshot = AgentTestData.Snapshot(AgentTestData.Item() with { DishName = "Mì Quảng", Price = 50000 });
        var context = new AgentContext
        {
            ConversationId = 7, UserId = 3, State = AgentTestData.State(), Source = plan == "none" ? null : snapshot,
            Request = new ANGI.Application.DTOs.Agent.SendAgentMessageRequestDto { ClientMessageId = 1, ExpectedConversationVersion = 1, Content = "xem lịch" },
            CurrentProposal = plan != "ready" ? null
                : new ANGI.Application.DTOs.Agent.AgentProposalDto(1, 1, "create", "ready", snapshot, new AgentValidation([], [], 50000, []), DateTime.UtcNow.AddHours(1))
        };

        var reply = AgentReplyBuilder.Answer(context, planAsked);

        // STEP-02: Assert reply starts with expected and reply.Contains() is plan == "saved".
        reply.Should().StartWith(expected);
        reply.Contains("Mì Quảng").Should().Be(plan == "saved");
    }

    // TEST-15: A tool notice still takes priority over every plan answer.
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

    // TEST-16: A widened meal is stated with the place, both radii and its days, grouped per meal; a meal not in the plan is not mentioned, and a plan without widening says nothing.
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

    // TEST-17: A preview names each itinerary day in order and how far a thin place reaches; a plan without an itinerary says nothing about it.
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

    // TEST-18: A preview with no budget says so; a preview with a budget does not.
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

    // TEST-19: The model's sentence replaces the fixed summary and closing; without it they are both shown.
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

}
