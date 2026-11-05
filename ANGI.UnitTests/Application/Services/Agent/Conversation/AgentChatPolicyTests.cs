using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Services.Agent.Conversation;
using FluentAssertions;

namespace ANGI.UnitTests.Application.Services.Agent.Conversation;

/// <summary>Checks explicit chat authorization separately from unconstrained natural-language model output.</summary>
public sealed class AgentChatPolicyTests
{
    // TEST-01: A staged change question invites a plain "ok" or "thôi" instead of asking for a formal confirmation.
    /// <summary>The question itself tells people a plain answer is enough.</summary>
    [Fact]
    public void StageChange_InvitesAPlainAnswer()
    {
        // STEP-01: Create current as AgentState.
        var current = new AgentState { NumDays = 3 };
        // STEP-02: Assert AgentConfirmationPolicy.StageChange().Reply contains the expected text.
        AgentConfirmationPolicy.StageChange(current, current with { NumDays = 4 }).Reply
            .Should().Contain("“ok”").And.Contain("“thôi”").And.NotContain("xác nhận thay đổi");
    }

    // TEST-02: Whole-message save instructions support Vietnamese accents and polite variants.
    /// <summary>Allows explicit saving without requiring a frontend command or proposal identifier.</summary>
    [Theory]
    [InlineData("lưu lịch trình này")]
    [InlineData("Tôi muốn lưu lịch này")]
    [InlineData("Xác nhận lưu lịch trình này!")]
    [InlineData("Lưu lịch trình này giúp tôi.")]
    [InlineData("save this itinerary")]
    [InlineData("Tôi muốn chốt lịch này")]
    [InlineData("chốt lịch trình này giúp tôi")]
    // STEP-01: Assert AgentConfirmationPolicy.IsSaveRequest() is true.
    public void IsSaveRequest_ExplicitInstruction_Authorizes(string text) => AgentConfirmationPolicy.IsSaveRequest(text).Should().BeTrue();

    // TEST-03: Negated, quoted, conditional and vague agreement text never authorizes an official write.
    /// <summary>Prevents model interpretation or substring matching from turning ordinary chat into a commit.</summary>
    [Theory]
    [InlineData("đồng ý")]
    [InlineData("ok")]
    [InlineData("đừng lưu lịch trình này")]
    [InlineData("không lưu lịch này")]
    [InlineData("lưu lịch trình này?")]
    [InlineData("\"lưu lịch trình này\"")]
    [InlineData("Nếu giá rẻ thì lưu lịch trình này")]
    [InlineData("Giải thích câu lưu lịch trình này")]
    [InlineData("đừng chốt lịch này")]
    [InlineData("chốt lịch này?")]
    [InlineData("hãy lập lịch giúp tôi để chốt lịch")]
    // STEP-01: Assert AgentConfirmationPolicy.IsSaveRequest() is false.
    public void IsSaveRequest_UncertainText_DoesNotAuthorize(string text) => AgentConfirmationPolicy.IsSaveRequest(text).Should().BeFalse();

    // TEST-04: A request to preview before saving counts as a review request, while its negated form does not, so neither authorizes a commit.
    /// <summary>Ensures a preview request is routed to review instead of being treated as permission to save.</summary>
    [Fact]
    public void IsReviewRequest_PreviewBeforeSaving_DoesNotAuthorizeCommit()
    {
        // STEP-01: Assert AgentConfirmationPolicy.IsReviewRequest() is true and AgentConfirmationPolicy.IsReviewRequest() is false.
        AgentConfirmationPolicy.IsReviewRequest("hãy lập lịch giúp tôi để chốt lịch").Should().BeTrue();
        AgentConfirmationPolicy.IsReviewRequest("đừng lập lịch giúp tôi để chốt lịch").Should().BeFalse();
    }

    // TEST-05: Constraint consent stages a detached proposal without changing the current hard limit.
    /// <summary>Ensures an explicit next-turn answer is required even when the model claims consent.</summary>
    [Fact]
    public void StageChange_Relaxation_KeepsCurrentConstraints()
    {
        // STEP-01: Derive current from a copy of AgentTestData.State() and get result from AgentConfirmationPolicy.StageChange.
        var current = AgentTestData.State() with { SourceRoadmapUpdatedAt = DateTime.UnixEpoch.AddSeconds(4), HistoryStartSequence = 8 };
        var result = AgentConfirmationPolicy.StageChange(current, current with { BudgetAmount = 700000 });
        // STEP-02: Assert result.State.BudgetAmount is 500000, result.State.PendingStateChange.BudgetAmount is 700000 and 1 more.
        result.State.BudgetAmount.Should().Be(500000);
        result.State.PendingStateChange!.BudgetAmount.Should().Be(700000);
        result.State.PendingStateChange.PendingStateChange.Should().BeNull();
        // STEP-03: Assert State.PendingStateChange.SourceRoadmapUpdatedAt is DateTime.UnixEpoch.AddSeconds(), result.State.PendingQuestion has a value and 2 more.
        result.State.PendingStateChange.SourceRoadmapUpdatedAt.Should().Be(DateTime.UnixEpoch.AddSeconds(4));
        result.State.PendingQuestion.Should().NotBeNullOrWhiteSpace();
        result.Snapshot.Should().BeNull(); result.Commit.Should().BeNull();
    }

    // TEST-06: The consent question names each removed, added and changed day/meal request and mentions an unchanged plan only when one exists; covers with and without a plan.
    /// <summary>Ensures the user can see exactly which meal request the model wants to change before agreeing.</summary>
    [Theory]
    [InlineData(true)][InlineData(false)]
    public void StageChange_MealRequests_NamesEachScopeAndPlanStatus(bool hasPlan)
    {
        // STEP-01: Derive current from a copy of AgentTestData.State(), derive proposed from a copy of current and derive reply from AgentConfirmationPolicy.StageChange().Reply.
        var current = AgentTestData.State() with { MealRequests = [new(1, "lunch", null, null, [6]), new(1, "dinner", null, null, [6])] };
        var proposed = current with { MealRequests = [new(1, "lunch", null, null, [6, 7]), new(2, null, null, null, [6])] };

        var reply = AgentConfirmationPolicy.StageChange(current, proposed, hasPlan).Reply;

        // STEP-02: Assert reply contains the expected text, depending on hasPlan, reply contains the expected text and reply does not contain the expected text.
        reply.Should().Contain("đổi yêu cầu món/quán cho bữa trưa ngày 1").And.Contain("bỏ yêu cầu món/quán cho bữa tối ngày 1")
            .And.Contain("thêm yêu cầu món/quán cho ngày 2");
        if (hasPlan) reply.Should().Contain("Bản lịch hiện tại chưa thay đổi");
        else reply.Should().NotContain("Bản lịch");
    }

    // TEST-07: A blocked save names the open question and the way out; a pending change or planning request keeps the general message.
    /// <summary>Prevents the user from being told to answer a question the message does not show.</summary>
    [Theory]
    [InlineData("question")][InlineData("change")][InlineData("planning")]
    public void SaveBlocked_ExplainsWhatIsOpen(string area)
    {
        // STEP-01: Create state as AgentState, when area == "change", set state to a copy of state, when area == "planning", set state to a copy of state and 1 more.
        var state = new AgentState { PendingQuestion = "Bạn muốn đổi bữa nào?" };
        if (area == "change") state = state with { PendingStateChange = new AgentState() };
        if (area == "planning") state = state with { PendingPlanningRequest = new(["x"]) };

        var reply = AgentConfirmationPolicy.SaveBlocked(state);

        // STEP-02: Assert depending on area == "question", reply contains the expected text, reply contains the expected text and reply is the expected text.
        if (area == "question") reply.Should().Contain("Bạn muốn đổi bữa nào?").And.Contain("giữ nguyên");
        else reply.Should().Be("Bạn cần trả lời câu hỏi hoặc xác nhận thay đổi điều kiện đang chờ trước khi lưu lịch.");
    }

    // TEST-08: Only a request to see the plan counts as a view request; covers accents, no accents, both word orders, a greeting and a question about the plan.
    /// <summary>The plan is listed again only for these messages, so an unrelated question never receives the whole itinerary.</summary>
    [Theory]
    [InlineData("Xem lịch", true)][InlineData("cho mình xem lại lịch", true)][InlineData("xem lo trinh", true)][InlineData("Liệt kê đề xuất", true)]
    [InlineData("Lịch hiện tại có gì?", true)][InlineData("lich the nao", true)]
    [InlineData("Xin chào", false)][InlineData("Giải thích bản nháp", false)][InlineData("Vì sao chọn món này", false)][InlineData("đổi bữa trưa", false)][InlineData("", false)]
    public void IsPlanViewRequest_MatchesOnlyRequestsToSeeThePlan(string content, bool expected) =>
        // STEP-01: Assert AgentConfirmationPolicy.IsPlanViewRequest() is expected.
        AgentConfirmationPolicy.IsPlanViewRequest(content).Should().Be(expected);
}
