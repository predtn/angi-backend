using ANGI.Application.Services.Agent;
using FluentAssertions;

namespace ANGI.UnitTests.Application.Services.Agent;

/// <summary>Verifies novelty wording is clarified by the backend instead of being looked up as a dish name or turned into an invented filter.</summary>
public sealed class AgentNoveltyPolicyTests
{
    // TEST-01: Novelty wording with an unresolved meaning asks the fixed novelty question; covers "chưa ăn bao giờ", "món gì lạ lạ",
    // "lạ miệng" with a scope, "món mới", "độc lạ", "chưa từng thử" and the unaccented "chua an bao gio".
    /// <summary>Ensures every common way of asking for new food reaches the same clarification.</summary>
    [Theory]
    [InlineData("Tôi muốn món chưa ăn bao giờ")]
    [InlineData("Cho tôi món gì lạ lạ")]
    [InlineData("Đổi bữa trưa ngày 2 sang món lạ miệng hơn")]
    [InlineData("Muốn thử món mới")]
    [InlineData("Có món nào độc lạ không")]
    [InlineData("Món tôi chưa từng thử")]
    [InlineData("toi muon mon chua an bao gio")]
    public void Question_NoveltyWording_AsksMeaning(string content)
    {
        var context = AgentTestData.Context(); context.Request.Content = content;

        AgentNoveltyPolicy.Question(context).Should().Be(AgentReplyBuilder.NoveltyQuestion);
    }

    // TEST-02: Look-alike words, refused novelty and wording already meaning "different from this plan" reach the model; covers sour taste,
    // "là", unaccented "mon la", adverbial "mới", a negation, a resolved request and "món chua".
    /// <summary>Keeps the rule narrow so ordinary sentences are never interrupted by the novelty question.</summary>
    [Theory]
    [InlineData("Tôi thích ăn chua")]
    [InlineData("Món này là món gì")]
    [InlineData("mon la gi vay")]
    [InlineData("Món này mới ngon")]
    [InlineData("Không cần món lạ đâu")]
    [InlineData("Cho tôi món lạ, khác các món trong lịch")]
    [InlineData("Món chua ăn với gì ngon")]
    public void Question_OrdinaryOrResolvedWording_ReachesModel(string content)
    {
        var context = AgentTestData.Context(); context.Request.Content = content;

        AgentNoveltyPolicy.Question(context).Should().BeNull();
    }

    // TEST-03: Over an existing plan, answering the novelty question with "never eaten" gets the honest no-history reply offering dishes
    // different from the plan, while "different from this plan" or a mixed answer that includes it reaches the model; covers all three.
    /// <summary>Prevents the model from inventing a never-eaten filter that ANGI has no data for.</summary>
    [Theory]
    [InlineData("Món chưa từng ăn", true)]
    [InlineData("Khác các món trong lịch hiện tại", false)]
    [InlineData("Chưa ăn bao giờ, khác lịch luôn", false)]
    public void Question_AnswerToNoveltyQuestion_IsHonestAboutHistory(string content, bool noHistory)
    {
        var context = AgentTestData.Context(AgentTestData.Snapshot(AgentTestData.Item())); context.Request.Content = content;
        context.State = context.State with { PendingQuestion = AgentReplyBuilder.NoveltyQuestion };

        var reply = AgentNoveltyPolicy.Question(context);

        if (noHistory) reply.Should().Be(AgentReplyBuilder.NoveltyWithoutHistory);
        else reply.Should().BeNull();
    }

    // TEST-04: Without any plan, the "never eaten" answer does not offer dishes "different from the current plan" but plans by taste.
    /// <summary>Avoids promising to avoid a plan that does not exist yet.</summary>
    [Fact]
    public void Question_NeverEatenWithoutPlan_PlansByTaste()
    {
        var context = AgentTestData.Context(); context.Request.Content = "Món chưa từng ăn";
        context.State = context.State with { PendingQuestion = AgentReplyBuilder.NoveltyQuestion };

        AgentNoveltyPolicy.Question(context).Should().Be(AgentReplyBuilder.NoveltyWithoutHistoryOrPlan)
            .And.Contain("Mình sẽ lập lịch theo các món hợp khẩu vị của bạn.").And.NotContain("lịch hiện tại");
    }
}
