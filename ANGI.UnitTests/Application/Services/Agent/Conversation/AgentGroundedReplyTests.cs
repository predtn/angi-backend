using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Services.Agent.Conversation;
using FluentAssertions;

namespace ANGI.UnitTests.Application.Services.Agent.Conversation;

/// <summary>Verifies AgentGroundedReply shows model wording, in replies and in preview notes, only when every number and proper name in it is backed by trusted data.</summary>
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
    public void IsGrounded_BackedWording_Passes(string reply) => AgentGroundedReply.IsGrounded(reply, Context()).Should().BeTrue();

    // TEST-02: Invented facts fall back; covers an unknown price, a thousand-unit price not in the plan, an unknown name mid-sentence, opening hours and fame.
    /// <summary>A failed check is invisible to the user; a false fact in a travel plan is not.</summary>
    [Theory]
    [InlineData("Mì Quảng giá 45.000 đồng.")]
    [InlineData("Món đó khoảng 45k thôi bạn.")]
    [InlineData("Bạn nên ghé Quán Bà Tám gần đó.")]
    [InlineData("Quán này mở cửa đến khuya nhé.")]
    [InlineData("Đây là quán nổi tiếng nhất vùng.")]
    public void IsGrounded_UnbackedFact_Fails(string reply) => AgentGroundedReply.IsGrounded(reply, Context()).Should().BeFalse();

    // TEST-03: Wording rules reject the rest; covers null, empty, 501 characters, English, a claimed change, a link, markup and internal terms.
    /// <summary>Only the backend may say a plan changed, and raw technical text must never reach a traveler.</summary>
    [Theory]
    [InlineData(null)][InlineData("")][InlineData("   ")][InlineData("Please clarify")][InlineData("Mình đã đổi món cho bạn nhé.")][InlineData("Xem ở https://món.vn nhé.")]
    [InlineData("<b>Chào</b> bạn nhé")][InlineData("Theo roadmap của bạn nhé.")][InlineData("Dữ liệu catalog chưa có món đó.")]
    public void IsGrounded_BadWording_Fails(string? reply) => AgentGroundedReply.IsGrounded(reply, Context()).Should().BeFalse();

    // TEST-04: Length boundary; covers exactly 500 and 501 characters.
    /// <summary>Keeps an answer conversational and bounds what model text can reach the user.</summary>
    [Theory]
    [InlineData(500, true)][InlineData(501, false)]
    public void IsGrounded_LengthBoundary(int length, bool expected)
    {
        var reply = "à" + new string('a', length - 1);

        AgentGroundedReply.IsGrounded(reply, Context()).Should().Be(expected);
    }

    // TEST-05: Names the user just wrote and tool results of this turn count as backed; covers both.
    /// <summary>Repeating the user's own words or a real lookup is not inventing anything.</summary>
    [Fact]
    public void IsGrounded_UserWordsAndToolResults_AreBacked()
    {
        var context = Context();
        context.Request.Content = "Có quán Hải Sản Bé Mặn không?";
        AgentGroundedReply.IsGrounded("Mình chưa thấy quán Hải Sản Bé Mặn trong lịch này.", context).Should().BeTrue();

        using var result = System.Text.Json.JsonDocument.Parse("""{"name":"Quán Cô Ba","price":70000}""");
        context.Exchanges.Add(new([], [result.RootElement.Clone()]));
        AgentGroundedReply.IsGrounded("Mình tìm thấy Quán Cô Ba, giá 70.000 đồng.", context).Should().BeTrue();
    }

    // TEST-06: Clarifications use model wording only for edit_scope and general with a real question; covers both, no question mark, a day restated and an unbacked number.
    /// <summary>A clarifying question should restate what was understood rather than ask it again.</summary>
    [Theory]
    [InlineData("edit_scope", "Bạn muốn đổi món nào trong ngày 2?", true)]
    [InlineData("general", "Bạn muốn đổi bữa nào của ngày 1 nhỉ?", true)]
    [InlineData("edit_scope", "Mình sẽ hỏi lại nhé.", false)]
    [InlineData("edit_scope", "Bạn muốn đổi món nào trong ngày 9?", false)]
    public void Choose_Clarify_UsesWordingOnlyWhenBacked(string kind, string reply, bool shown)
    {
        var context = Context();
        var chosen = AgentGroundedReply.Choose(context, new AgentOutput { Action = "clarify", Clarification = kind, Reply = reply });

        if (shown) chosen.Should().Be(reply);
        else chosen.Should().Be(AgentReplyBuilder.Clarify(context, kind));
    }

    // TEST-07: Question kinds tied to backend state or evidence keep fixed wording, whatever the model writes; covers taste, novelty, coverage, budget and catalog questions.
    /// <summary>The taste state machine recognizes its questions by exact text, so rewording them would break it.</summary>
    [Theory]
    [InlineData("taste_intensity")][InlineData("taste_scope")][InlineData("novelty")][InlineData("coverage")][InlineData("budget")][InlineData("catalog_choice")][InlineData("catalog_support")]
    public void Choose_EvidenceBasedClarify_KeepsFixedWording(string kind)
    {
        var context = Context();

        AgentGroundedReply.Choose(context, new AgentOutput { Action = "clarify", Clarification = kind, Reply = "Bạn muốn gì nào?" })
            .Should().Be(AgentReplyBuilder.Clarify(context, kind));
    }

    // TEST-08: Missing trip details, a tool notice and searches already run keep the fixed question; covers all three.
    /// <summary>Backend-known gaps must be named exactly, so the next turn has what planning requires.</summary>
    [Theory]
    [InlineData("trip")][InlineData("notice")][InlineData("searched")]
    public void Choose_BackendKnownGap_KeepsFixedQuestion(string area)
    {
        var context = Context();
        if (area == "trip") context.State = context.State with { BudgetAmount = null };
        if (area == "notice") context.ToolNotice = "Chưa lấy được gợi ý.";
        if (area == "searched") context.Exchanges.Add(new([], []));

        AgentGroundedReply.Choose(context, new AgentOutput { Action = "clarify", Clarification = "general", Reply = "Bạn muốn đổi món nào?" })
            .Should().Be(AgentReplyBuilder.Clarify(context, "general"));
    }

    // TEST-09: Answers show backed wording, and fall back when unbacked, when a tool notice exists or when the user asked to see the plan.
    /// <summary>Trusted outcomes and the full itinerary stay backend-built.</summary>
    [Theory]
    [InlineData("Vì sao chọn món này?", "Món này còn trong ngân sách của bạn nhé.", null, true)]
    [InlineData("Vì sao chọn món này?", "Món này giá 45.000 đồng.", null, false)]
    [InlineData("Vì sao chọn món này?", "Món này còn trong ngân sách của bạn nhé.", "Chưa lấy được gợi ý.", false)]
    [InlineData("Xem lại lịch giúp mình", "Lịch của bạn còn trong ngân sách nhé.", null, false)]
    public void Choose_Answer_ShowsOnlyGroundedWording(string content, string reply, string? notice, bool shown)
    {
        var context = Context();
        context.Request.Content = content;
        context.ToolNotice = notice;

        var chosen = AgentGroundedReply.Choose(context, new AgentOutput { Action = "answer", Reply = reply });

        if (shown) chosen.Should().Be(reply);
        else chosen.Should().Be(AgentReplyBuilder.Answer(context));
    }

    // TEST-10: An opener is friendly wording with no fact; covers a good one, the 60 and 61 character boundary, digits, a name, a claim, English and no ending mark.
    /// <summary>It is written before backend repairs may change dishes, so it can never state one.</summary>
    [Theory]
    [InlineData("Xong rồi nhé!", "Xong rồi nhé!")][InlineData("  Xong rồi nhé.  ", "Xong rồi nhé.")]
    [InlineData("Giá 50 nghìn nhé!", null)][InlineData("Mình chọn Bún Chả nhé!", null)][InlineData("Mình đã đổi món nhé!", null)][InlineData("Done!", null)][InlineData("Xong rồi nhé", null)][InlineData(null, null)]
    public void Opener_AcceptsOnlyFactFreeWording(string? reply, string? expected) => AgentGroundedReply.Opener(reply).Should().Be(expected);

    // TEST-11: Opener length boundary; covers exactly 60 and 61 characters.
    /// <summary>An opener stays a short clause in front of the backend facts.</summary>
    [Theory]
    [InlineData(60, true)][InlineData(61, false)]
    public void Opener_LengthBoundary(int length, bool accepted)
    {
        var reply = "à" + new string('a', length - 2) + "!";

        (AgentGroundedReply.Opener(reply) is not null).Should().Be(accepted);
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

    // TEST-12: Natural wording about the final dishes is kept as written.
    /// <summary>This is the point of the feature: each dish and each day explains itself.</summary>
    [Fact]
    public void AttachNotes_VerifiableWording_IsKept()
    {
        var result = AgentGroundedReply.AttachNotes(AgentTestData.Snapshot(Quang, Cha), NotesContext(
            notes: [new(1, "Mì Quảng có sợi mì vàng và nước dùng sánh.", "Hợp bữa trưa nhẹ mà vẫn no.")],
            days: [new(1, "Ngày này đi từ món nước sang món nướng nên không bị ngán.")]));

        result.DishNotes.Should().ContainSingle().Which.Should().Be(new AgentDishNote(1, "Mì Quảng có sợi mì vàng và nước dùng sánh.", "Hợp bữa trưa nhẹ mà vẫn no."));
        result.DaySummaries.Should().ContainSingle().Which.Summary.Should().StartWith("Ngày này");
    }

    // TEST-13: Wording the backend cannot verify is dropped; covers an invented price, an unknown name, fame, hours, internal terms and excess length.
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
        var result = AgentGroundedReply.AttachNotes(AgentTestData.Snapshot(Quang), NotesContext(notes: [new(1, text, text)], days: [new(1, text)]));

        result.DishNotes.Should().BeEmpty();
        result.DaySummaries.Should().BeEmpty();
    }

    // TEST-14: One bad sentence does not cost the good one.
    /// <summary>Keeps what can be verified from a partly invented note.</summary>
    [Fact]
    public void AttachNotes_OneBadSentence_KeepsTheOther()
    {
        var result = AgentGroundedReply.AttachNotes(AgentTestData.Snapshot(Quang), NotesContext(notes: [new(1, "Sợi mì vàng, nước dùng sánh.", "Chỉ 45.000 đồng.")]));

        result.DishNotes.Should().ContainSingle().Which.Should().Be(new AgentDishNote(1, "Sợi mì vàng, nước dùng sánh.", null));
    }

    // TEST-15: A note about a dish the backend swapped out afterwards never shows.
    /// <summary>The notes run after the repairs, so the card cannot describe a dish that is no longer there.</summary>
    [Fact]
    public void AttachNotes_DishNoLongerInPreview_IsDropped()
    {
        var result = AgentGroundedReply.AttachNotes(AgentTestData.Snapshot(Quang), NotesContext(notes: [new(9, "Món đã bị thay ra.", "Không còn trong lịch.")]));

        result.DishNotes.Should().BeEmpty();
    }

    // TEST-16: A day summary may name only that day's dishes, so one written before a swap is not shown.
    /// <summary>Stops a conclusion that praises a dish from another day or one that was replaced.</summary>
    [Fact]
    public void AttachNotes_SummaryNamingAnotherDaysDish_IsDropped()
    {
        var result = AgentGroundedReply.AttachNotes(AgentTestData.Snapshot(Quang, Com), NotesContext(days: [new(1, "Mở đầu nhẹ nhàng rồi kết bằng Cơm Tấm.")]));

        result.DaySummaries.Should().BeEmpty();
    }

    // TEST-17: An edit keeps what it did not touch: other dishes' notes and unchanged days' summaries stay, a changed day's summary goes.
    /// <summary>A small edit does not make the rest of the card go blank, nor leave a conclusion about dishes that left.</summary>
    [Fact]
    public void AttachNotes_Edit_KeepsOnlyWhatStillApplies()
    {
        var source = AgentTestData.Snapshot(Quang, Com) with
        {
            DishNotes = [new(1, "Giới thiệu cũ.", "Lý do cũ."), new(3, "Giới thiệu cơm.", "Lý do cơm.")],
            DaySummaries = [new(1, "Kết luận ngày đầu."), new(2, "Kết luận ngày hai.")]
        };

        var result = AgentGroundedReply.AttachNotes(AgentTestData.Snapshot(Cha, Com), NotesContext(source, notes: [new(2, "Bún chả nướng thơm.", "Đổi vị cho bữa tối.")]));

        result.DishNotes!.Select(note => note.DishId).Should().Equal(2, 3);
        result.DaySummaries.Should().ContainSingle().Which.Should().Be(new AgentDaySummary(2, "Kết luận ngày hai."));
    }

    // TEST-18: A preview made without the model, such as removing a dish, keeps the notes it still has.
    /// <summary>Non-model turns must not wipe the card.</summary>
    [Fact]
    public void AttachNotes_NoModelOutput_KeepsEarlierNotes()
    {
        var source = AgentTestData.Snapshot(Quang, Cha) with { DishNotes = [new(1, "Giới thiệu cũ.", null)], DaySummaries = [new(1, "Kết luận cũ.")] };

        var result = AgentGroundedReply.AttachNotes(AgentTestData.Snapshot(Quang), AgentTestData.Context(source));

        result.DishNotes.Should().ContainSingle().Which.DishId.Should().Be(1);
        result.DaySummaries.Should().BeEmpty();
    }
}
