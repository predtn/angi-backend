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

    [Fact]
    public void IsReviewRequest_PreviewBeforeSaving_DoesNotAuthorizeCommit()
    {
        AgentChatPolicy.IsReviewRequest("hãy lập lịch giúp tôi để chốt lịch").Should().BeTrue();
        AgentChatPolicy.IsReviewRequest("đừng lập lịch giúp tôi để chốt lịch").Should().BeFalse();
    }

    // TEST-03: Constraint consent stages a detached proposal without changing the current hard limit.
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
}
