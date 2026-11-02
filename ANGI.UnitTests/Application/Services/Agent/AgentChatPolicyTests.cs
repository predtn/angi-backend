using ANGI.Application.Services.Agent;
using FluentAssertions;

namespace ANGI.UnitTests.Application.Services.Agent;

/// <summary>Checks explicit chat authorization separately from unconstrained natural-language model output.</summary>
public sealed class AgentChatPolicyTests
{
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
}
