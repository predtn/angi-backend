using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Services.Agent;
using FluentAssertions;

namespace ANGI.UnitTests.Application.Services.Agent;

/// <summary>Checks explicit chat authorization separately from unconstrained natural-language model output.</summary>
public sealed class AgentChatPolicyTests
{
    /// <summary>A staged change or a save question accepts how people really say yes, so nobody has to type a set phrase.</summary>
    [Theory]
    [InlineData("ok")] [InlineData("Oke nhé")] [InlineData("được")] [InlineData("được rồi")] [InlineData("ừ")] [InlineData("uh")] [InlineData("ờ")]
    [InlineData("vâng")] [InlineData("dạ")] [InlineData("có")] [InlineData("đúng rồi")] [InlineData("đồng ý")] [InlineData("dc")] [InlineData("oki")]
    [InlineData("yes")] [InlineData("chuẩn")] [InlineData("sao cũng được")] [InlineData("xác nhận thay đổi")] [InlineData("👍")]
    public void Yes_CommonSlang_IsAccepted(string text)
    {
        AgentChatPolicy.AcceptsChange(text).Should().BeTrue();
    }

    /// <summary>Short commands that only make sense for a change do not count as agreeing to save.</summary>
    [Theory]
    [InlineData("đổi đi")] [InlineData("quất")] [InlineData("triển đi")] [InlineData("làm đi")]
    public void ChangeCommands_AcceptChangeButNotSave(string text)
    {
        AgentChatPolicy.AcceptsChange(text).Should().BeTrue();
        AgentChatPolicy.AcceptsSave(text).Should().BeFalse();
    }

    /// <summary>The same common wording declines, in any of the places a yes/no question is open.</summary>
    [Theory]
    [InlineData("không")] [InlineData("ko")] [InlineData("k")] [InlineData("khum")] [InlineData("thôi")] [InlineData("thôi khỏi")] [InlineData("khỏi")]
    [InlineData("giữ nguyên")] [InlineData("no")] [InlineData("không đồng ý")] [InlineData("chưa đâu")] [InlineData("hủy")]
    public void No_CommonSlang_IsRejected(string text)
    {
        AgentChatPolicy.RejectsChange(text).Should().BeTrue();
        AgentChatPolicy.DeclinesSave(text).Should().BeTrue();
        AgentChatPolicy.DismissesQuestion(text).Should().BeTrue();
        AgentChatPolicy.AcceptsChange(text).Should().BeFalse();
    }

    /// <summary>Only a whole-message answer counts: a longer sentence or "dừng" (not "đúng") goes on to be read properly.</summary>
    [Theory]
    [InlineData("dừng")] [InlineData("Dừng lại")] [InlineData("ok nhưng đổi món")] [InlineData("không ok")] [InlineData("được không")] [InlineData("có món chay không")]
    public void Yes_OnlyAWholeMessageAnswer_Counts(string text)
    {
        AgentChatPolicy.AcceptsChange(text).Should().BeFalse();
        AgentChatPolicy.AcceptsSave(text).Should().BeFalse();
    }

    /// <summary>The question itself tells people a plain answer is enough.</summary>
    [Fact]
    public void StageChange_InvitesAPlainAnswer()
    {
        var current = new AgentState { NumDays = 3 };
        AgentChatPolicy.StageChange(current, current with { NumDays = 4 }).Reply
            .Should().Contain("“ok”").And.Contain("“thôi”").And.NotContain("xác nhận thay đổi");
    }

    // TEST-01: Whole-message save instructions support Vietnamese accents and polite variants.
    /// <summary>Allows explicit saving without requiring a frontend command or proposal identifier.</summary>
    [Theory]
    [InlineData("lưu lịch trình này")]
    [InlineData("Tôi muốn lưu lịch này")]
    [InlineData("Xác nhận lưu lịch trình này!")]
    [InlineData("Lưu lịch trình này giúp tôi.")]
    [InlineData("save this itinerary")]
    [InlineData("Tôi muốn chốt lịch này")]
    [InlineData("chốt lịch trình này giúp tôi")]
    public void IsSaveRequest_ExplicitInstruction_Authorizes(string text) => AgentChatPolicy.IsSaveRequest(text).Should().BeTrue();

    // TEST-02: Negated, quoted, conditional and vague agreement text never authorizes an official write.
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
    public void IsSaveRequest_UncertainText_DoesNotAuthorize(string text) => AgentChatPolicy.IsSaveRequest(text).Should().BeFalse();

    // TEST-03: A request to preview before saving counts as a review request, while its negated form does not, so neither authorizes a commit.
    /// <summary>Ensures a preview request is routed to review instead of being treated as permission to save.</summary>
    [Fact]
    public void IsReviewRequest_PreviewBeforeSaving_DoesNotAuthorizeCommit()
    {
        AgentChatPolicy.IsReviewRequest("hãy lập lịch giúp tôi để chốt lịch").Should().BeTrue();
        AgentChatPolicy.IsReviewRequest("đừng lập lịch giúp tôi để chốt lịch").Should().BeFalse();
    }

    // TEST-04: Constraint consent stages a detached proposal without changing the current hard limit.
    /// <summary>Ensures an explicit next-turn answer is required even when the model claims consent.</summary>
    [Fact]
    public void StageChange_Relaxation_KeepsCurrentConstraints()
    {
        var current = AgentTestData.State() with { SourceRoadmapUpdatedAt = DateTime.UnixEpoch.AddSeconds(4), HistoryStartSequence = 8 };
        var result = AgentChatPolicy.StageChange(current, current with { BudgetAmount = 700000 });
        result.State.BudgetAmount.Should().Be(500000);
        result.State.PendingStateChange!.BudgetAmount.Should().Be(700000);
        result.State.PendingStateChange.PendingStateChange.Should().BeNull();
        result.State.PendingStateChange.SourceRoadmapUpdatedAt.Should().Be(DateTime.UnixEpoch.AddSeconds(4));
        result.State.PendingQuestion.Should().NotBeNullOrWhiteSpace();
        result.Snapshot.Should().BeNull(); result.Commit.Should().BeNull();
    }

    // TEST-05: The consent question names each removed, added and changed day/meal request and mentions an unchanged plan only when one exists; covers with and without a plan.
    /// <summary>Ensures the user can see exactly which meal request the model wants to change before agreeing.</summary>
    [Theory]
    [InlineData(true)][InlineData(false)]
    public void StageChange_MealRequests_NamesEachScopeAndPlanStatus(bool hasPlan)
    {
        var current = AgentTestData.State() with { MealRequests = [new(1, "lunch", null, null, [6]), new(1, "dinner", null, null, [6])] };
        var proposed = current with { MealRequests = [new(1, "lunch", null, null, [6, 7]), new(2, null, null, null, [6])] };

        var reply = AgentChatPolicy.StageChange(current, proposed, hasPlan).Reply;

        reply.Should().Contain("đổi yêu cầu món/quán cho bữa trưa ngày 1").And.Contain("bỏ yêu cầu món/quán cho bữa tối ngày 1")
            .And.Contain("thêm yêu cầu món/quán cho ngày 2");
        if (hasPlan) reply.Should().Contain("Bản lịch hiện tại chưa thay đổi");
        else reply.Should().NotContain("Bản lịch");
    }

    // TEST-06: A bare save intent is recognized, while the explicit save wording, negations, questions and longer sentences are not;
    // covers "lưu", "Lưu lại đi", "chốt đi", "ok lưu", "save" and their rejected counterparts.
    /// <summary>Routes "lưu" to a save reminder instead of the model, without widening the explicit commit wording.</summary>
    [Theory]
    [InlineData("lưu", true)]
    [InlineData("Lưu lại đi!", true)]
    [InlineData("chốt đi", true)]
    [InlineData("ok lưu", true)]
    [InlineData("save", true)]
    [InlineData("lưu lịch trình này", false)]
    [InlineData("đừng lưu", false)]
    [InlineData("lưu?", false)]
    [InlineData("lưu món này vào yêu thích", false)]
    public void IsShortSaveRequest_BareIntentOnly(string text, bool expected) => AgentChatPolicy.IsShortSaveRequest(text).Should().Be(expected);

    // TEST-07: Declining a clarification is recognized; covers each accepted phrase and wording that asks for something else.
    /// <summary>Gives the user a free way out of a question so the current plan can be saved.</summary>
    [Theory]
    [InlineData("không", true)][InlineData("Thôi", true)][InlineData("giữ nguyên", true)][InlineData("không đổi nữa", true)][InlineData("không cần", true)]
    [InlineData("đổi bữa trưa", false)][InlineData("có", false)]
    public void DismissesQuestion_ReadsWholeMessage(string content, bool expected)
    {
        AgentChatPolicy.DismissesQuestion(content).Should().Be(expected);
    }

    // TEST-08: A blocked save names the open question and the way out; a pending change or planning request keeps the general message.
    /// <summary>Prevents the user from being told to answer a question the message does not show.</summary>
    [Theory]
    [InlineData("question")][InlineData("change")][InlineData("planning")]
    public void SaveBlocked_ExplainsWhatIsOpen(string area)
    {
        var state = new AgentState { PendingQuestion = "Bạn muốn đổi bữa nào?" };
        if (area == "change") state = state with { PendingStateChange = new AgentState() };
        if (area == "planning") state = state with { PendingPlanningRequest = new(["x"]) };

        var reply = AgentChatPolicy.SaveBlocked(state);

        if (area == "question") reply.Should().Contain("Bạn muốn đổi bữa nào?").And.Contain("giữ nguyên");
        else reply.Should().Be("Bạn cần trả lời câu hỏi hoặc xác nhận thay đổi điều kiện đang chờ trước khi lưu lịch.");
    }

    // TEST-09: Only a request to see the plan counts as a view request; covers accents, no accents, both word orders, a greeting and a question about the plan.
    /// <summary>The plan is listed again only for these messages, so an unrelated question never receives the whole itinerary.</summary>
    [Theory]
    [InlineData("Xem lịch", true)][InlineData("cho mình xem lại lịch", true)][InlineData("xem lo trinh", true)][InlineData("Liệt kê đề xuất", true)]
    [InlineData("Lịch hiện tại có gì?", true)][InlineData("lich the nao", true)]
    [InlineData("Xin chào", false)][InlineData("Giải thích bản nháp", false)][InlineData("Vì sao chọn món này", false)][InlineData("đổi bữa trưa", false)][InlineData("", false)]
    public void IsPlanViewRequest_MatchesOnlyRequestsToSeeThePlan(string content, bool expected) =>
        AgentChatPolicy.IsPlanViewRequest(content).Should().Be(expected);
}
