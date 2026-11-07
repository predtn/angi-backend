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

    // TEST-02: Constraint consent stages a detached proposal without changing the current hard limit.
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

    // TEST-03: The consent question names each removed, added and changed day/meal request and mentions an unchanged plan only when one exists; covers with and without a plan.
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

    // TEST-04: A blocked save names the open question and the way out; a pending change or planning request keeps the general message.
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

    // TEST-05: Only the whole reset sentence matches, with or without accents, case or a final mark; a longer message, a discard sentence or blank text does not.
    /// <summary>Keeps the reset sentence of the deleted-roadmap error recognizable without reading any other wording.</summary>
    [Theory]
    [InlineData("làm lại từ đầu", true)][InlineData("  Làm lại từ đầu!  ", true)][InlineData("lam lai tu dau.", true)][InlineData("Bắt đầu kế hoạch mới", true)]
    [InlineData("start over", true)][InlineData("làm lại từ đầu giúp mình đi", false)][InlineData("bỏ bản nháp", false)][InlineData("", false)][InlineData("   ", false)]
    public void IsResetPhrase_MatchesOnlyTheWholeResetSentence(string content, bool expected)
    {
        // STEP-01: Assert AgentConfirmationPolicy.IsResetPhrase(content) equals expected.
        AgentConfirmationPolicy.IsResetPhrase(content).Should().Be(expected);
    }
}
