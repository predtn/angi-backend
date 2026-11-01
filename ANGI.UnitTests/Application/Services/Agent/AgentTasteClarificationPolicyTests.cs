using ANGI.Application.Common.Interfaces.Services.Agent;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Services.Agent;
using ANGI.UnitTests.Application.UseCases.Agent;
using FluentAssertions;
using Moq;

namespace ANGI.UnitTests.Application.Services.Agent;

/// <summary>Checks that direct taste requests are clarified before ranking, without changing reco or profile weights.</summary>
public sealed class AgentTasteClarificationPolicyTests
{
    // TEST-01: An ambiguous taste preference asks the user before calling the model or ranking; covers missing intensity, missing scope and both missing, with a persisted model guess that must not count as an answer.
    /// <summary>Avoids spending model and reco calls on a taste preference the user has not made precise yet.</summary>
    [Theory]
    [InlineData("Tôi muốn lên lịch ăn ở Hà Nội 3 ngày, ngân sách 500k 1 ngày, tôi muốn ăn cay", true, true)]
    [InlineData("Tôi muốn lên lịch ăn uống Hà Nội 3 ngày, ngân sách 500000 đồng 1 ngày, tôi thích ăn chua", true, true)]
    [InlineData("Tôi muốn lên lịch ăn uống Hà Nội 5 ngày, ngân sách 500000 đồng 1 ngày, tôi thích ăn cay", true, true)]
    [InlineData("Lập lịch 5 ngày, ngân sách 500k 1 ngày, thích ăn cay", true, true)]
    [InlineData("Tôi thích ăn cay cho toàn chuyến", true, false)]
    [InlineData("Tôi thích ăn cay vừa", false, true)]
    [InlineData("Tôi muốn ăn cay ngày 2", true, false)]
    [InlineData("Tôi thích ăn chua", true, true)]
    [InlineData("Tôi thích ăn cay nhưng không quá nhiều cho cả chuyến", true, false)]
    [InlineData("Tôi thích ăn cay với ngân sách vừa phải cho cả chuyến", true, false)]
    public async Task AmbiguousPreference_AsksBeforeCallingModelOrRanking(string content, bool intensity, bool scope)
    {
        var fixture = new AgentUseCaseFixture();
        var context = AgentTestData.Context(AgentTestData.Snapshot(AgentTestData.Item()));
        context.Request.Content = content;
        // A persisted guess from an older model response must not authorize that same guess again.
        context.State = context.State with { TasteTargets = [new("spicy", .75)] };
        var baseline = context.State;
        var result = await fixture.Orchestrator().RunAsync(context, default);
        result.Reply.Contains("Bạn muốn mức ", StringComparison.Ordinal).Should().Be(intensity);
        result.Reply.Contains("toàn chuyến", StringComparison.Ordinal).Should().Be(scope);
        (result.State with { PendingPlanningRequest = null }).Should().Be(baseline with { PendingQuestion = result.Reply });
        result.State.PendingPlanningRequest!.Messages.Should().Contain(content);
        result.Snapshot.Should().BeNull(); result.Commit.Should().BeNull();
        context.Grants.Should().BeEmpty();
        fixture.Llm.Verify(x => x.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-02: Resolved or non-preference text does not invent a clarification; covers stated intensity and scope, dish names, negation, quoted text and a restaurant name.
    /// <summary>Prevents unnecessary questions when the text is already precise or is not a preference.</summary>
    [Theory]
    [InlineData("Tôi thích ăn cay vừa cho toàn chuyến")]
    [InlineData("Tôi muốn ăn rất cay vào bữa tối ngày 2")]
    [InlineData("Tôi thích ăn chua nhẹ cho cả chuyến")]
    [InlineData("Tôi thích ăn cay vừa cho cả 5 ngày")]
    [InlineData("Tôi thích ăn mì cay ở Hà Nội")]
    [InlineData("Tôi không thích ăn cay")]
    [InlineData("Giải thích câu 'tôi thích ăn cay'")]
    [InlineData("Nhà hàng có tên “Tôi thích ăn cay”")]
    public void ResolvedOrNonPreferenceText_DoesNotInventAClarification(string content)
    {
        var context = AgentTestData.Context(); context.Request.Content = content;
        AgentTasteClarificationPolicy.Question(context).Should().BeNull();
    }

    // TEST-03: Partial answers keep asking only for the part that is still missing.
    /// <summary>Ensures the user is never asked again for a part of the preference already answered.</summary>
    [Fact]
    public void PartialAnswers_KeepAskingOnlyWhatIsStillMissing()
    {
        var context = AgentTestData.Context();
        context.Request.Content = "Tôi thích ăn cay";
        var initialQuestion = AgentTasteClarificationPolicy.Question(context)!;
        context.History.Add(new("user", context.Request.Content));
        context.History.Add(new("assistant", initialQuestion));
        context.State = context.State with { PendingQuestion = initialQuestion };
        context.Request.Content = "Vừa";
        var scopeQuestion = AgentTasteClarificationPolicy.Question(context)!;
        scopeQuestion.Should().Contain("toàn chuyến").And.NotContain("Bạn muốn mức ");
        context.History.Add(new("user", "Vừa"));
        context.History.Add(new("assistant", scopeQuestion));
        context.State = context.State with { PendingQuestion = scopeQuestion };
        context.Request.Content = "Cả chuyến";
        AgentTasteClarificationPolicy.Question(context).Should().BeNull();
        context.State.TasteTargets.Should().BeEmpty();
    }

    // TEST-04: A taste resolution the user already gave is reused, while a model-guessed target alone is not trusted.
    /// <summary>Ensures only user-provided resolutions are reused, never model guesses.</summary>
    [Fact]
    public void KnownHumanResolution_IsReusedWithoutTrustingModelGuesses()
    {
        var context = AgentTestData.Context();
        context.State = context.State with { TasteTargets = [new("spicy", .5)] };
        context.History.Add(new("user", "Tôi thích ăn cay vừa cho cả chuyến"));
        context.Request.Content = "Lập lịch 5 ngày, thích ăn cay";
        AgentTasteClarificationPolicy.Question(context).Should().BeNull();
    }

    // TEST-05: When the scope is already known, a short intensity answer completes the question.
    /// <summary>Ensures a short intensity reply is enough once the scope is known.</summary>
    [Fact]
    public void ScopeAlreadySupplied_PartialIntensityAnswerCompletesTheQuestion()
    {
        var context = AgentTestData.Context();
        context.History.Add(new("user", "Tôi thích ăn cay cho cả chuyến"));
        context.State = context.State with { PendingQuestion = "Bạn muốn mức cay như thế nào: không, nhẹ, vừa, nhiều hay rất nhiều?" };
        context.Request.Content = "Vừa";
        AgentTasteClarificationPolicy.Question(context).Should().BeNull();
        context.Request.Content = "Nhiều quán hơn";
        AgentTasteClarificationPolicy.Question(context).Should().Contain("Bạn muốn mức cay");
    }

    // TEST-06: Confirming a constraint change does not confirm a taste that still lacks intensity and scope.
    /// <summary>Prevents a constraint confirmation from being read as an answer to an open taste question.</summary>
    [Fact]
    public void ConstraintConfirmation_DoesNotConfirmAnUnspecifiedTaste()
    {
        var context = AgentTestData.Context();
        context.History.Add(new("user", "Lập lịch 5 ngày, thích ăn cay"));
        context.Request.Content = "Xác nhận thay đổi";
        context.ReplanAfterConfirmation = true;
        AgentTasteClarificationPolicy.Question(context).Should().Contain("Bạn muốn mức cay").And.Contain("toàn chuyến");
    }

    // TEST-07: A different taste does not inherit the intensity given for another taste.
    /// <summary>Ensures each taste gets its own intensity instead of reusing another taste's answer.</summary>
    [Fact]
    public void DifferentTaste_DoesNotInheritAnotherTastesIntensity()
    {
        var context = AgentTestData.Context();
        context.History.Add(new("user", "Tôi thích ăn cay vừa cho toàn chuyến"));
        context.State = context.State with { PendingQuestion = "Bạn muốn áp dụng khẩu vị cay cho toàn chuyến hay ngày/bữa cụ thể nào?" };
        context.Request.Content = "Tôi thích ăn ngọt";
        AgentTasteClarificationPolicy.Question(context).Should().Contain("Bạn muốn mức ngọt").And.Contain("toàn chuyến");
    }
}
