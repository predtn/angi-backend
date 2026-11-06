using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Services.Agent.Conversation;
using FluentAssertions;

namespace ANGI.UnitTests.Application.Services.Agent.Conversation;

/// <summary>Verifies AgentReplyGuard shows model wording, in replies and in preview notes, only when every number and proper name in it is backed by trusted data.</summary>
public sealed class AgentGroundedReplyTests
{
    // TEST-01: Natural answers built from real data pass; covers a price, a thousand-unit price, a total, a day number, a dish, a restaurant and an admitted gap.
    /// <summary>This is the point of the change: a direct question gets a direct, natural answer.</summary>
    [Theory]
    [InlineData("Bữa trưa ngày 1 là Mì Quảng ở Quán Hương, giá 50.000 đồng nhé.")]
    [InlineData("Món đó khoảng 50k thôi bạn.")]
    [InlineData("Cả lịch khoảng 80.000 đồng một người.")]
    [InlineData("Mình chọn Bún Chả cho ngày 2 vì đang còn trong ngân sách của bạn.")]
    [InlineData("Mình chưa có thông tin giờ mở cửa của quán này.")]
    // STEP-01: Assert AgentReplyGuard.IsGrounded() is true.
    public void IsGrounded_BackedWording_Passes(string reply) => AgentReplyGuard.IsGrounded(reply, Context()).Should().BeTrue();

    // TEST-02: Invented facts fall back; covers an unknown price, a thousand-unit price not in the plan, an unknown name mid-sentence, opening hours and fame.
    /// <summary>A failed check is invisible to the user; a false fact in a travel plan is not.</summary>
    [Theory]
    [InlineData("Mì Quảng giá 45.000 đồng.")]
    [InlineData("Món đó khoảng 45k thôi bạn.")]
    [InlineData("Bạn nên ghé Quán Bà Tám gần đó.")]
    [InlineData("Quán này mở cửa đến khuya nhé.")]
    [InlineData("Đây là quán nổi tiếng nhất vùng.")]
    // STEP-01: Assert AgentReplyGuard.IsGrounded() is false.
    public void IsGrounded_UnbackedFact_Fails(string reply) => AgentReplyGuard.IsGrounded(reply, Context()).Should().BeFalse();

    // TEST-03: Wording rules reject the rest; covers null, empty, 501 characters, English, a claimed change, a link, markup and internal terms.
    /// <summary>Only the backend may say a plan changed, and raw technical text must never reach a traveler.</summary>
    [Theory]
    [InlineData(null)][InlineData("")][InlineData("   ")][InlineData("Please clarify")][InlineData("Mình đã đổi món cho bạn nhé.")][InlineData("Xem ở https://món.vn nhé.")]
    [InlineData("<b>Chào</b> bạn nhé")][InlineData("Theo roadmap của bạn nhé.")][InlineData("Dữ liệu catalog chưa có món đó.")]
    // STEP-01: Assert AgentReplyGuard.IsGrounded() is false.
    public void IsGrounded_BadWording_Fails(string? reply) => AgentReplyGuard.IsGrounded(reply, Context()).Should().BeFalse();

    // TEST-04: Length boundary; covers exactly 500 and 501 characters.
    /// <summary>Keeps an answer conversational and bounds what model text can reach the user.</summary>
    [Theory]
    [InlineData(500, true)][InlineData(501, false)]
    public void IsGrounded_LengthBoundary(int length, bool expected)
    {
        // STEP-01: Derive reply from the expected value.
        var reply = "à" + new string('a', length - 1);

        // STEP-02: Assert AgentReplyGuard.IsGrounded() is expected.
        AgentReplyGuard.IsGrounded(reply, Context()).Should().Be(expected);
    }

    // TEST-05: Names the user just wrote and tool results of this turn count as backed; covers both.
    /// <summary>Repeating the user's own words or a real lookup is not inventing anything.</summary>
    [Fact]
    public void IsGrounded_UserWordsAndToolResults_AreBacked()
    {
        // STEP-01: Get context from Context and set context.Request.Content to a test text.
        var context = Context();
        context.Request.Content = "Có quán Hải Sản Bé Mặn không?";
        // STEP-02: Read result from the response; assert AgentReplyGuard.IsGrounded() is true.
        AgentReplyGuard.IsGrounded("Mình chưa thấy quán Hải Sản Bé Mặn trong lịch này.", context).Should().BeTrue();

        using var result = System.Text.Json.JsonDocument.Parse("""{"name":"Quán Cô Ba","price":70000}""");
        // STEP-03: Add the computed value to context.Exchanges.
        context.Exchanges.Add(new([], [result.RootElement.Clone()]));
        // STEP-04: Assert AgentReplyGuard.IsGrounded() is true.
        AgentReplyGuard.IsGrounded("Mình tìm thấy Quán Cô Ba, giá 70.000 đồng.", context).Should().BeTrue();
    }

    // TEST-06: Clarifications use model wording only for edit_scope and general with a real question; covers both, a question ending in "nhé." instead of "?", a day restated and an unbacked number.
    /// <summary>A clarifying question should restate what was understood rather than ask it again.</summary>
    [Theory]
    [InlineData("edit_scope", "Bạn muốn đổi món nào trong ngày 2?", true)]
    [InlineData("general", "Bạn muốn đổi bữa nào của ngày 1 nhỉ?", true)]
    [InlineData("edit_scope", "Bạn nói giúp mình ngày và bữa muốn đổi nhé.", true)]
    [InlineData("edit_scope", "Bạn muốn đổi món nào trong ngày 9?", false)]
    public void Choose_Clarify_UsesWordingOnlyWhenBacked(string kind, string reply, bool shown)
    {
        // STEP-01: Get context from Context and get chosen from AgentReplyGuard.Choose.
        var context = Context();
        var chosen = AgentReplyGuard.Choose(context, new AgentOutput { Action = "clarify", Clarification = kind, Reply = reply });

        // STEP-02: Assert depending on shown, chosen is reply and chosen is AgentReplyBuilder.Clarify().
        if (shown) chosen.Should().Be(reply);
        else chosen.Should().Be(AgentReplyBuilder.Clarify(context, kind));
    }

    // TEST-07: Question kinds tied to backend state or evidence keep fixed wording, whatever the model writes; covers taste, novelty, coverage, budget and catalog questions.
    /// <summary>These rest on what the backend observed, so the model cannot word them.</summary>
    [Theory]
    [InlineData("coverage")][InlineData("budget")][InlineData("catalog_choice")][InlineData("catalog_support")]
    public void Choose_EvidenceBasedClarify_KeepsFixedWording(string kind)
    {
        // STEP-01: Get context from Context.
        var context = Context();

        // STEP-02: Assert AgentReplyGuard.Choose() is AgentReplyBuilder.Clarify().
        AgentReplyGuard.Choose(context, new AgentOutput { Action = "clarify", Clarification = kind, Reply = "Bạn muốn gì nào?" })
            .Should().Be(AgentReplyBuilder.Clarify(context, kind));
    }

    // TEST-08: A tool notice and a new plan that searched but was not assembled keep the fixed question; covers both.
    /// <summary>Backend-known gaps must be named exactly, so the next turn has what planning requires.</summary>
    [Theory]
    [InlineData("notice")][InlineData("searched")]
    public void Choose_BackendKnownGap_KeepsFixedQuestion(string area)
    {
        // STEP-01: Get context from Context and set the notice or the searches on a new plan from area.
        var context = area == "searched" ? NewPlan() : Context();
        if (area == "notice") context.ToolNotice = "Chưa lấy được gợi ý.";

        // STEP-02: Assert AgentReplyGuard.Choose() is AgentReplyBuilder.Clarify().
        AgentReplyGuard.Choose(context, new AgentOutput { Action = "clarify", Clarification = "general", Reply = "Bạn muốn đổi món nào?" })
            .Should().Be(AgentReplyBuilder.Clarify(context, "general"));
    }

    // TEST-09: After tools the model words its own question unless a new plan stalled; covers an edit of a plan, a taste question on a new plan, a missing value on a new plan, and each stalled kind.
    /// <summary>Most questions after a search are ordinary; only one that hides a plan the model could not assemble must stay fixed.</summary>
    [Theory]
    [InlineData("plan", "general", true)]
    [InlineData("plan", "edit_scope", true)]
    [InlineData("new", "taste_intensity", true)]
    [InlineData("new_missing", "trip_details", true)]
    [InlineData("new", "general", false)]
    [InlineData("new", "trip_details", false)]
    [InlineData("new", "edit_scope", false)]
    public void Choose_AfterTools_ShownUnlessPlanStalled(string setup, string kind, bool shown)
    {
        // STEP-01: Get context from setup and add one tool exchange.
        var context = setup == "plan" ? Context() : NewPlan();
        if (setup == "plan") context.Exchanges.Add(new([], []));
        if (setup == "new_missing") context.State = context.State with { BudgetAmount = null };
        const string reply = "Bạn muốn thế nào nhỉ?";

        // STEP-02: Assert ChooseIssue is empty when shown and after_tools otherwise.
        AgentReplyGuard.ChooseIssue(context, new AgentOutput { Action = "clarify", Clarification = kind, Reply = reply }).Should().Be(shown ? null : "after_tools");
    }

    // TEST-10: Answers show backed wording, and fall back when unbacked or when a tool notice exists; the user's words alone never list the plan.
    /// <summary>Trusted outcomes and the full itinerary stay backend-built.</summary>
    [Theory]
    [InlineData("Vì sao chọn món này?", "Món này còn trong ngân sách của bạn nhé.", null, true)]
    [InlineData("Vì sao chọn món này?", "Món này giá 45.000 đồng.", null, false)]
    [InlineData("Vì sao chọn món này?", "Món này còn trong ngân sách của bạn nhé.", "Chưa lấy được gợi ý.", false)]
    [InlineData("Xem lại lịch giúp mình", "Lịch của bạn còn trong ngân sách nhé.", null, true)]
    public void Choose_Answer_ShowsOnlyGroundedWording(string content, string reply, string? notice, bool shown)
    {
        // STEP-01: Get context from Context, set context.Request.Content to content, set context.ToolNotice to notice and get chosen from AgentReplyGuard.Choose.
        var context = Context();
        context.Request.Content = content;
        context.ToolNotice = notice;

        var chosen = AgentReplyGuard.Choose(context, new AgentOutput { Action = "answer", Reply = reply });

        // STEP-02: Assert depending on shown, chosen is reply and chosen is AgentReplyBuilder.Answer().
        if (shown) chosen.Should().Be(reply);
        else chosen.Should().Be(AgentReplyBuilder.Answer(context));
    }

    // TEST-11: The {plan} slot is filled with the backend's own plan text; covers words around it, the slot alone, an unbacked fact around it and no plan to fill.
    /// <summary>The model decides that the user wants to see the plan, in any wording; the plan itself always comes from the backend.</summary>
    [Theory]
    [InlineData("Lịch của bạn đây nhé: {plan}", true, "Lịch của bạn đây nhé: ")]
    [InlineData("{plan}", true, "")]
    [InlineData("Lịch giá 45.000 đồng nè: {plan}", true, "")]
    [InlineData("Lịch của bạn đây nhé: {plan}", false, null)]
    public void Choose_PlanSlot_IsFilledByTheBackend(string reply, bool hasPlan, string? lead)
    {
        // STEP-01: Get context from Context, with or without a plan, and get chosen from AgentReplyGuard.Choose.
        var context = Context();
        if (!hasPlan) context.Source = null;

        var chosen = AgentReplyGuard.Choose(context, new AgentOutput { Action = "answer", Reply = reply });

        // STEP-02: Assert the plan text follows the model's words, or the fixed answer when there is no plan.
        if (lead is null) chosen.Should().Be(AgentReplyBuilder.Answer(context)).And.NotContain("{plan}");
        else chosen.Should().Be((lead + AgentReplyBuilder.Plan(context)).Trim());
    }

    // TEST-12: An opener is friendly wording with no fact; covers a good one, the 60 and 61 character boundary, digits, a name, a claim, English and no ending mark.
    /// <summary>It is written before backend repairs may change dishes, so it can never state one.</summary>
    [Theory]
    [InlineData("Xong rồi nhé!", "Xong rồi nhé!")][InlineData("  Xong rồi nhé.  ", "Xong rồi nhé.")]
    [InlineData("Giá 50 nghìn nhé!", null)][InlineData("Mình chọn Bún Chả nhé!", null)][InlineData("Mình đã đổi món nhé!", null)][InlineData("Done!", null)][InlineData("Xong rồi nhé", null)][InlineData(null, null)]
    // STEP-01: Assert AgentReplyGuard.Opener() is expected.
    public void Opener_AcceptsOnlyFactFreeWording(string? reply, string? expected) => AgentReplyGuard.Opener(reply).Should().Be(expected);

    // TEST-13: Opener length boundary; covers exactly 120 and 121 characters.
    /// <summary>An opener stays a short clause in front of the backend facts.</summary>
    [Theory]
    [InlineData(120, true)][InlineData(121, false)]
    public void Opener_LengthBoundary(int length, bool accepted)
    {
        // STEP-01: Derive reply from the expected value.
        var reply = "à" + new string('a', length - 2) + "!";

        // STEP-02: Assert AgentReplyGuard.Opener(reply) is not null is accepted.
        (AgentReplyGuard.Opener(reply) is not null).Should().Be(accepted);
    }

    // TEST-14: Code is told from Vietnamese; covers a function call, backticks, an assignment, brackets, a closing semicolon, a plain sentence and parentheses around words.
    /// <summary>Stops a reply that carries out a coding request even when the model labels it as small talk.</summary>
    [Theory]
    [InlineData("print(\"hello\")", true)][InlineData("Bạn dùng `map` nhé", true)][InlineData("giá trị x = 5", true)][InlineData("mảng [1, 2]", true)]
    [InlineData("int x;", true)][InlineData("Mình vẫn ở đây nè!", false)][InlineData("Bữa trưa (món chính) nhé.", false)]
    // STEP-01: Assert AgentReplyGuard.IsCode() is expected.
    public void IsCode_TellsCodeFromVietnamese(string text, bool expected) => AgentReplyGuard.IsCode(text).Should().Be(expected);

    // TEST-15: "Sẽ đổi" is a promise only when stated; covers a question, a statement and a past claim, which stays rejected even as a question.
    /// <summary>Lets the model ask about a change naturally, while only the backend may say a change happened.</summary>
    [Theory]
    [InlineData("Mình sẽ đổi món nào cho bạn nhé?", true)][InlineData("Mình sẽ đổi món cho bạn nhé.", false)][InlineData("Mình đã đổi món rồi, được không?", false)]
    // STEP-01: Assert AgentReplyGuard.IsGrounded() is expected.
    public void IsGrounded_Promise_AllowedOnlyInAQuestion(string reply, bool expected) => AgentReplyGuard.IsGrounded(reply, Context()).Should().Be(expected);

    // TEST-16: The model words the questions of a new trip and the other open questions; covers each kind, a missing place, a tool result and no question mark, and a kind resting on backend evidence stays fixed.
    /// <summary>Keeps every question resting on backend evidence in its fixed wording, and only those.</summary>
    [Theory]
    [InlineData("trip_details", true, 0, "Không sao đâu, bạn ước chừng một mức được không?", true)]
    [InlineData("general", true, 0, "Không sao đâu, bạn ước chừng một mức được không?", true)]
    [InlineData("taste_intensity", true, 0, "Bạn thích cay nhẹ hay vừa?", true)]
    [InlineData("novelty", false, 0, "Món lạ là khác lịch này hay món bạn chưa từng ăn?", true)]
    [InlineData("trip_details", false, 0, "Bạn muốn ăn ở đâu nè?", true)]
    [InlineData("place_scope", true, 0, "Bạn muốn tìm toàn thành phố hay quanh một khu?", true)]
    [InlineData("preferences", true, 0, "Bạn có khẩu vị riêng gì không?", true)]
    [InlineData("coverage", true, 0, "Bạn muốn đổi điều kiện gì không?", false)]
    [InlineData("trip_details", true, 1, "Không sao đâu, bạn ước chừng một mức được không?", true)]
    [InlineData("trip_details", true, 0, "Không sao đâu, bạn ước chừng một mức nhé.", true)]
    public void Choose_TripQuestion_WordedOnlyWhenAllowed(string kind, bool placeKnown, int toolRounds, string reply, bool shown)
    {
        // STEP-01: Get context from Context, set context.State to a copy of context.State and 2 more.
        var context = Context();
        context.State = context.State with { Province = placeKnown ? context.State.Province : null, BudgetAmount = null };
        for (var round = 0; round < toolRounds; round++) context.Exchanges.Add(new([], []));

        var result = AgentReplyGuard.Choose(context, new AgentOutput { Action = "clarify", Clarification = kind, Reply = reply });

        // STEP-02: Assert result is the expected value.
        result.Should().Be(shown ? reply : AgentReplyBuilder.Clarify(context, kind));
    }

    // TEST-17: A name a tool returned backs the wording even with Vietnamese letters, which the stored JSON keeps as escapes.
    /// <summary>Without this every accented dish from a tool result counts as invented and the natural reply is thrown away.</summary>
    [Fact]
    public void IsGrounded_NameFromToolResult_Passes()
    {
        // STEP-01: Get context from Context and add one tool exchange carrying a Vietnamese dish name.
        var context = Context();
        context.Exchanges.Add(new([], [System.Text.Json.JsonSerializer.SerializeToElement(new { name = "Bún Riêu" }, AgentJson.Options)]));

        // STEP-02: Assert AgentReplyGuard.IsGrounded() is true.
        AgentReplyGuard.IsGrounded("Bạn thử Bún Riêu nhé.", context).Should().BeTrue();
    }

    // TEST-18: Only a word that starts at least five dish names counts; covers four and five names, case, accents and an empty catalog.
    /// <summary>Rare first words such as "lòng" or "ba" are everyday Vietnamese and must not block a normal sentence.</summary>
    [Fact]
    public void DishWords_KeepOnlyCommonFirstWords()
    {
        // STEP-01: Prepare names with five dishes starting "Phở" in mixed case and four starting "Lòng".
        var names = new[] { "Phở Bò", "phở gà", "PHỞ CUỐN", "Phở Trộn", "Phở Khô", "Lòng Xào", "Lòng Nướng", "Lòng Heo", "Lòng Lợn" };

        // STEP-02: Assert AgentReplyGuard.DishWords() holds only "phở", and nothing for an empty catalog.
        AgentReplyGuard.DishWords(names).Should().BeEquivalentTo("phở");
        AgentReplyGuard.DishWords([]).Should().BeEmpty();
    }

    // TEST-19: A common dish word needs backing; covers an invented one, one the user wrote, one from the plan, one from a tool result, a look-alike without its accent, and a reply with none.
    /// <summary>Catches an invented dish in lower case, which the capital-letter check cannot see.</summary>
    [Theory]
    [InlineData("Món của bạn là phở đó.", "Món này thế nào?", false)]
    [InlineData("Món của bạn là phở đó.", "Phở ở đâu ngon?", true)]
    [InlineData("Bún ở lịch này khá ổn đó.", "Món này thế nào?", true)]
    [InlineData("Cá viên này khá ổn đó.", "Món này thế nào?", true)]
    [InlineData("Cả chuyến đi đều ổn đó.", "Món này thế nào?", true)]
    [InlineData("Mình chưa biết món bạn thích nhất.", "Món này thế nào?", true)]
    public void IsGrounded_DishWord_NeedsBacking(string reply, string content, bool grounded)
    {
        // STEP-01: Get context from Context, set the message, the dish words and a tool result naming "Cá Viên".
        var context = Context();
        context.Request.Content = content;
        context.DishWords = new HashSet<string> { "phở", "bún", "cá" };
        context.Exchanges.Add(new([], [System.Text.Json.JsonSerializer.SerializeToElement(new { name = "Cá Viên" }, AgentJson.Options)]));

        // STEP-02: Assert AgentReplyGuard.IsGrounded() is grounded.
        AgentReplyGuard.IsGrounded(reply, context).Should().Be(grounded);
    }

    // TEST-20: The words around slots may only use what the user wrote; covers plain words, the user's own figure and dish, a written price, a plan's dish, a tool's name and an unbacked dish word.
    /// <summary>Every fact belongs in a slot, so a correct name written out is still refused.</summary>
    [Theory]
    [InlineData("Bạn thử ở nhé!", true)]
    [InlineData("Dưới 60k có món ở nhé!", true)]
    [InlineData("Dưới 60.000 đồng có món ở nhé!", true)]
    [InlineData("Phở thì bạn thử ở nhé!", true)]
    [InlineData("Giá 50.000 đồng nhé!", false)]
    [InlineData("Bạn thử Mì Quảng nhé!", false)]
    [InlineData("Bạn thử Bún Riêu nhé!", false)]
    [InlineData("Hoặc bún cũng được nhé!", false)]
    [InlineData("Ví dụ bữa tối ngày 2 nha!", true)]
    [InlineData("Ví dụ bữa tối ngày 99 nha!", false)]
    public void IsGroundedTemplate_OnlyUsesWhatTheUserWrote(string text, bool grounded)
    {
        // STEP-01: Get context from Context, set the message, the dish words and a tool result naming a dish.
        var context = Context();
        context.Request.Content = "gợi ý phở dưới 60k";
        context.DishWords = new HashSet<string> { "phở", "bún" };
        context.Exchanges.Add(new([], [System.Text.Json.JsonSerializer.SerializeToElement(new { name = "Bún Riêu" }, AgentJson.Options)]));

        // STEP-02: Assert AgentReplyGuard.IsGroundedTemplate() is grounded.
        AgentReplyGuard.IsGroundedTemplate(text, context).Should().Be(grounded);
    }

    // TEST-21: A refused wording is reported with the rule it broke; covers empty, too long, English, markup, code, a change claim, an internal term, fame, a number, a name, a dish word and a backed reply.
    /// <summary>The code goes to the log instead of the wording, so a review can see why replies are replaced.</summary>
    [Theory]
    [InlineData("", 0, "empty")]
    [InlineData("Dài quá", 501, "too_long")]
    [InlineData("Your plan is ready", 0, "not_vietnamese")]
    [InlineData("Lịch <b>đẹp</b> nhé", 0, "markup")]
    [InlineData("Bạn dùng print(\"đẹp\") nhé", 0, "code")]
    [InlineData("Mình đã đổi món rồi nhé", 0, "claim")]
    [InlineData("Mình sẽ đổi món nhé", 0, "claim")]
    [InlineData("Hỏi roadmap của bạn nhé", 0, "internal_term")]
    [InlineData("Đây là quán nổi tiếng nhé", 0, "claim")]
    [InlineData("Món đó khoảng 45.000 đồng nhé", 0, "number")]
    [InlineData("Bạn nên ghé Quán Bà Tám nhé", 0, "name")]
    [InlineData("Món của bạn là phở đó nhé", 0, "dish_word")]
    [InlineData("Bữa trưa ngày 1 là Mì Quảng ở Quán Hương nhé", 0, null)]
    public void GroundingIssue_NamesTheRule(string reply, int length, string? issue)
    {
        // STEP-01: Get context from Context and set its dish words.
        var context = Context();
        context.DishWords = new HashSet<string> { "phở" };

        // STEP-02: Assert AgentReplyGuard.GroundingIssue() is issue.
        AgentReplyGuard.GroundingIssue(length > 0 ? reply + new string('!', length) : reply, context).Should().Be(issue);
    }

    // TEST-22: A question the model cannot word is reported with why; covers a backend question kind, a tool notice, a searched turn, a question ending in "nhé." and a shown question.
    /// <summary>Separates what the rules forbid from what the model got wrong; conversations 197 and 198 lost natural questions only because they ended in "nhé." instead of "?".</summary>
    [Theory]
    [InlineData("coverage", "Bạn muốn gì nào?", "kind", "fixed_question")]
    [InlineData("trip_details", "Bạn đi đâu nào?", "noplace", null)]
    [InlineData("general", "Bạn muốn gì nào?", "notice", "backend_notice")]
    [InlineData("general", "Bạn muốn gì nào?", "searched", "after_tools")]
    [InlineData("general", "Bạn nhắn mình khẩu vị nhé.", "kind", null)]
    [InlineData("general", "Bạn muốn gì nào?", "kind", null)]
    public void ChooseIssue_NamesWhyAQuestionIsReplaced(string kind, string reply, string setup, string? issue)
    {
        // STEP-01: Get context from Context and apply setup.
        var context = Context();
        if (setup == "noplace") context.State = context.State with { Province = null };
        if (setup == "notice") context.ToolNotice = "Chưa lấy được gợi ý.";
        if (setup == "searched") context = NewPlan();

        // STEP-02: Assert AgentReplyGuard.ChooseIssue() is issue.
        AgentReplyGuard.ChooseIssue(context, new AgentOutput { Action = "clarify", Clarification = kind, Reply = reply }).Should().Be(issue);
    }

    /// <summary>A new plan with every trip value known and one tool exchange, the shape of a turn that searched and has not assembled yet.</summary>
    private static AgentContext NewPlan()
    {
        var context = AgentTestData.Context();
        context.Exchanges.Add(new([], []));
        return context;
    }

    /// <summary>Builds a two-day plan with one dish per day, real names and prices.</summary>
    private static AgentContext Context()
    {
        var snapshot = AgentTestData.Snapshot(
            AgentTestData.Item(1, "a", 1, "lunch") with { DishName = "Mì Quảng", RestaurantName = "Quán Hương", Price = 50000 },
            AgentTestData.Item(2, "b", 2, "dinner") with { DishName = "Bún Chả", RestaurantName = "Quán Lan", Price = 30000 });
        var context = AgentTestData.Context(snapshot);
        context.Request.Content = "Món này thế nào?";
        return context;
    }

    private static readonly AgentItem Quang = AgentTestData.Item(1, "a", 1, "lunch") with { DishName = "Mì Quảng", RestaurantName = "Quán Hương", Price = 50000 };
    private static readonly AgentItem Cha = AgentTestData.Item(2, "b", 1, "dinner") with { DishName = "Bún Chả", RestaurantName = "Quán Lan", Price = 30000 };
    private static readonly AgentItem Com = AgentTestData.Item(3, "c", 2, "lunch") with { DishName = "Cơm Tấm", RestaurantName = "Quán Bảy", Price = 40000 };

    /// <summary>Builds a context whose model output carries the given dish notes and day summaries.</summary>
    private static AgentContext NotesContext(AgentSnapshot? source = null, AgentDishNote[]? notes = null, AgentDaySummary[]? days = null)
    {
        var context = AgentTestData.Context(source);
        context.PreviewOutput = new AgentOutput { Action = "create_proposal", Reply = "Xong rồi nhé!", DishNotes = notes ?? [], DaySummaries = days ?? [] };
        return context;
    }

    // TEST-23: Natural wording about the final dishes is kept as written.
    /// <summary>This is the point of the feature: each dish and each day explains itself.</summary>
    [Fact]
    public void AttachNotes_VerifiableWording_IsKept()
    {
        // STEP-01: Get result from AgentReplyGuard.AttachNotes.
        var result = AgentReplyGuard.AttachNotes(AgentTestData.Snapshot(Quang, Cha), NotesContext(
            notes: [new(1, "Mì Quảng có sợi mì vàng và nước dùng sánh.", "Hợp bữa trưa nhẹ mà vẫn no.")],
            days: [new(1, "Ngày này đi từ món nước sang món nướng nên không bị ngán.")]));

        // STEP-02: Assert result.DishNotes has exactly one matching item, result.DishNotes is a new AgentDishNote and 2 more.
        result.DishNotes.Should().ContainSingle().Which.Should().Be(new AgentDishNote(1, "Mì Quảng có sợi mì vàng và nước dùng sánh.", "Hợp bữa trưa nhẹ mà vẫn no."));
        result.DaySummaries.Should().ContainSingle().Which.Summary.Should().StartWith("Ngày này");
    }

    // TEST-24: Wording the backend cannot verify is dropped; covers an invented price, an unknown name, fame, hours, internal terms and excess length.
    /// <summary>A missing sentence is invisible to the user; a false fact in a travel plan is not.</summary>
    [Theory]
    [InlineData("Món này chỉ 45.000 đồng thôi.")]
    [InlineData("Ngon hơn hẳn ở Quán Bà Tám gần đó.")]
    [InlineData("Đây là quán nổi tiếng nhất vùng.")]
    [InlineData("Quán này mở cửa đến khuya.")]
    [InlineData("Theo catalog thì món này hợp.")]
    [InlineData("Plain English sentence only.")]
    public void AttachNotes_UnverifiableWording_IsDropped(string text)
    {
        // STEP-01: Get result from AgentReplyGuard.AttachNotes.
        var result = AgentReplyGuard.AttachNotes(AgentTestData.Snapshot(Quang), NotesContext(notes: [new(1, text, text)], days: [new(1, text)]));

        // STEP-02: Assert result.DishNotes is empty and result.DaySummaries is empty.
        result.DishNotes.Should().BeEmpty();
        result.DaySummaries.Should().BeEmpty();
    }

    // TEST-25: One bad sentence does not cost the good one.
    /// <summary>Keeps what can be verified from a partly invented note.</summary>
    [Fact]
    public void AttachNotes_OneBadSentence_KeepsTheOther()
    {
        // STEP-01: Get result from AgentReplyGuard.AttachNotes.
        var result = AgentReplyGuard.AttachNotes(AgentTestData.Snapshot(Quang), NotesContext(notes: [new(1, "Sợi mì vàng, nước dùng sánh.", "Chỉ 45.000 đồng.")]));

        // STEP-02: Assert result.DishNotes has exactly one matching item and result.DishNotes is a new AgentDishNote.
        result.DishNotes.Should().ContainSingle().Which.Should().Be(new AgentDishNote(1, "Sợi mì vàng, nước dùng sánh.", null));
    }

    // TEST-26: A note about a dish the backend swapped out afterwards never shows.
    /// <summary>The notes run after the repairs, so the card cannot describe a dish that is no longer there.</summary>
    [Fact]
    public void AttachNotes_DishNoLongerInPreview_IsDropped()
    {
        // STEP-01: Get result from AgentReplyGuard.AttachNotes.
        var result = AgentReplyGuard.AttachNotes(AgentTestData.Snapshot(Quang), NotesContext(notes: [new(9, "Món đã bị thay ra.", "Không còn trong lịch.")]));

        // STEP-02: Assert result.DishNotes is empty.
        result.DishNotes.Should().BeEmpty();
    }

    // TEST-27: A day summary may name only that day's dishes, so one written before a swap is not shown.
    /// <summary>Stops a conclusion that praises a dish from another day or one that was replaced.</summary>
    [Fact]
    public void AttachNotes_SummaryNamingAnotherDaysDish_IsDropped()
    {
        // STEP-01: Get result from AgentReplyGuard.AttachNotes.
        var result = AgentReplyGuard.AttachNotes(AgentTestData.Snapshot(Quang, Com), NotesContext(days: [new(1, "Mở đầu nhẹ nhàng rồi kết bằng Cơm Tấm.")]));

        // STEP-02: Assert result.DaySummaries is empty.
        result.DaySummaries.Should().BeEmpty();
    }

    // TEST-28: An edit keeps what it did not touch: other dishes' notes and unchanged days' summaries stay, a changed day's summary goes.
    /// <summary>A small edit does not make the rest of the card go blank, nor leave a conclusion about dishes that left.</summary>
    [Fact]
    public void AttachNotes_Edit_KeepsOnlyWhatStillApplies()
    {
        // STEP-01: Derive source from a copy of AgentTestData.Snapshot() and get result from AgentReplyGuard.AttachNotes.
        var source = AgentTestData.Snapshot(Quang, Com) with
        {
            DishNotes = [new(1, "Giới thiệu cũ.", "Lý do cũ."), new(3, "Giới thiệu cơm.", "Lý do cơm.")],
            DaySummaries = [new(1, "Kết luận ngày đầu."), new(2, "Kết luận ngày hai.")]
        };

        var result = AgentReplyGuard.AttachNotes(AgentTestData.Snapshot(Cha, Com), NotesContext(source, notes: [new(2, "Bún chả nướng thơm.", "Đổi vị cho bữa tối.")]));

        // STEP-02: Assert result.DishNotes.Select(filtered) equals 2, result.DaySummaries has exactly one matching item and result.DaySummaries is a new AgentDaySummary.
        result.DishNotes!.Select(note => note.DishId).Should().Equal(2, 3);
        result.DaySummaries.Should().ContainSingle().Which.Should().Be(new AgentDaySummary(2, "Kết luận ngày hai."));
    }

    // TEST-29: A preview made without the model, such as removing a dish, keeps the notes it still has.
    /// <summary>Non-model turns must not wipe the card.</summary>
    [Fact]
    public void AttachNotes_NoModelOutput_KeepsEarlierNotes()
    {
        // STEP-01: Derive source from a copy of AgentTestData.Snapshot() and get result from AgentReplyGuard.AttachNotes.
        var source = AgentTestData.Snapshot(Quang, Cha) with { DishNotes = [new(1, "Giới thiệu cũ.", null)], DaySummaries = [new(1, "Kết luận cũ.")] };

        var result = AgentReplyGuard.AttachNotes(AgentTestData.Snapshot(Quang), AgentTestData.Context(source));

        // STEP-02: Assert result.DishNotes has exactly one matching item, result.DishNotes.DishId is 1 and result.DaySummaries is empty.
        result.DishNotes.Should().ContainSingle().Which.DishId.Should().Be(1);
        result.DaySummaries.Should().BeEmpty();
    }
}
