using ANGI.Application.Common.Interfaces.Services.Agent;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Services.Agent.Conversation;
using ANGI.Application.Services.Agent.Intake;
using ANGI.UnitTests.Application.UseCases.Agent;
using FluentAssertions;
using Moq;

namespace ANGI.UnitTests.Application.Services.Agent.Intake;

/// <summary>Checks that direct taste requests are clarified before ranking, without changing reco or profile weights, and that novelty wording is clarified instead of being looked up as a dish name or turned into an invented filter.</summary>
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

    // TEST-05: When the scope is already known, a short intensity answer completes the question, and an unrelated reply
    // ends it instead of repeating the same question.
    /// <summary>Ensures a short intensity reply is enough once the scope is known and a non-answer never traps the user in a loop.</summary>
    [Fact]
    public void ScopeAlreadySupplied_PartialIntensityAnswerCompletesTheQuestion()
    {
        var context = AgentTestData.Context();
        context.History.Add(new("user", "Tôi thích ăn cay cho cả chuyến"));
        context.State = context.State with { PendingQuestion = "Bạn muốn mức cay như thế nào: không, nhẹ, vừa, nhiều hay rất nhiều?" };
        context.Request.Content = "Vừa";
        AgentTasteClarificationPolicy.Question(context).Should().BeNull();
        context.Request.Content = "Nhiều quán hơn";
        AgentTasteClarificationPolicy.Question(context).Should().BeNull();
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

    // TEST-08: A numbered or ordinal meal of a day is an explicit scope, so no scope question is asked; covers bữa 2, bữa thứ hai, bữa đầu and bữa cuối.
    /// <summary>Ensures "bữa 2 ngày 1" is read as a meal scope instead of only "ngày 1" or no scope at all.</summary>
    [Theory]
    [InlineData("Tôi thích ăn chua vừa cho bữa 2 ngày 1")]
    [InlineData("Tôi thích ăn chua vừa cho bữa thứ hai")]
    [InlineData("Tôi thích ăn chua vừa cho bữa đầu")]
    [InlineData("Tôi thích ăn chua vừa cho bữa cuối ngày 3")]
    public void NumberedMealScope_IsAnExplicitScope(string content)
    {
        var context = AgentTestData.Context();
        context.Request.Content = content;

        AgentTasteClarificationPolicy.Question(context).Should().BeNull();
    }

    // TEST-09: Only a declared taste preference counts as taste wording; covers a declaration, an edit request and a negated preference.
    /// <summary>Limits meal-scope narrowing evidence to messages that actually state a taste.</summary>
    [Theory]
    [InlineData("Tôi thích ăn chua vừa cho bữa 2 ngày 1", true)]
    [InlineData("Đổi món khác cho bữa 1 ngày 1", false)]
    [InlineData("Tôi không thích ăn cay", false)]
    public void StatesTaste_OnlyForDeclaredPreferences(string content, bool expected)
    {
        AgentTasteClarificationPolicy.StatesTaste(content).Should().Be(expected);
    }

    // TEST-10: Look-alike words typed with diacritics never become a taste, while correct spellings and unaccented messages still do;
    // covers chưa, mận, đang, cây, tuổi, accented chua/mặn/ngọt, and fully unaccented text.
    /// <summary>Prevents "món chưa ăn bao giờ" from being read as a sour preference because diacritics were stripped before matching.</summary>
    [Theory]
    [InlineData("Tôi muốn món chưa ăn bao giờ", false)]
    [InlineData("Tôi thích ăn mận", false)]
    [InlineData("Tôi muốn đang ăn gì đó nhẹ", false)]
    [InlineData("Tôi thích cây xanh", false)]
    [InlineData("Tôi muốn tuổi trẻ", false)]
    [InlineData("Tôi thích ăn chua", true)]
    [InlineData("Tôi thích ăn mặn", true)]
    [InlineData("Tôi muốn ăn ngọt", true)]
    [InlineData("toi thich an chua", true)]
    public void StatesTaste_LookAlikeWords_AreNotTastes(string content, bool expected)
    {
        AgentTasteClarificationPolicy.StatesTaste(content).Should().Be(expected);
    }

    // TEST-11: While a taste question is open, a reply with no intensity, scope, taste or trip facts ends it, while a budget-only or
    // scope-only reply keeps asking the missing part; covers a denial, a budget answer and a scope answer.
    /// <summary>Stops the question loop for a non-answer without losing partial answers to the combined question.</summary>
    [Theory]
    [InlineData("Tôi đâu có nói chua, ý tôi là món lạ", false)]
    [InlineData("500k 1 ngày", true)]
    [InlineData("toàn chuyến", true)]
    public void OpenTasteQuestion_NonAnswerEndsIt(string content, bool asksAgain)
    {
        var context = AgentTestData.Context();
        context.History.Add(new("user", "Tôi thích ăn chua"));
        context.State = context.State with { PendingQuestion = "Bạn muốn mức chua như thế nào: không, nhẹ, vừa, nhiều hay rất nhiều? Bạn muốn áp dụng khẩu vị chua cho toàn chuyến hay ngày/bữa cụ thể nào?" };
        context.Request.Content = content;

        var question = AgentTasteClarificationPolicy.Question(context);

        if (asksAgain) question.Should().StartWith("Bạn muốn mức chua");
        else question.Should().BeNull();
    }

    // TEST-12: Limiting wording names a taste without a level and is clarified with the fixed taste question; covers "đừng cay quá",
    // "không cay quá cho cả chuyến" and "bớt ngọt ngày 2".
    /// <summary>Asks the specific taste question before the model instead of a generic one when the user limits a taste.</summary>
    [Theory]
    [InlineData("Cho tôi món gì đó hợp khẩu vị, đừng cay quá", "Bạn muốn mức cay", true)]
    [InlineData("Không cay quá cho cả chuyến", "Bạn muốn mức cay", false)]
    [InlineData("Bớt ngọt ngày 2", "Bạn muốn mức ngọt", false)]
    public void LimitingTaste_AsksFixedQuestion(string content, string expected, bool asksScope)
    {
        var context = AgentTestData.Context();
        context.Request.Content = content;

        var question = AgentTasteClarificationPolicy.Question(context);

        question.Should().StartWith(expected);
        question!.Contains("toàn chuyến hay ngày/bữa").Should().Be(asksScope);
    }

    // TEST-13: Mentioned tastes are listed by display name once each and look-alike words are ignored; covers two tastes and a "chưa" message.
    /// <summary>Supplies the taste names used by the backend-rendered taste questions.</summary>
    [Theory]
    [InlineData("Đừng cay quá, ít ngọt thôi, cay vừa", new[] { "cay", "ngọt" })]
    [InlineData("Món này chưa ngon", new string[0])]
    public void MentionedTastes_NamesTastesIgnoringLookAlikes(string content, string[] expected)
    {
        AgentTasteClarificationPolicy.MentionedTastes(content).Should().Equal(expected);
    }

    // TEST-14: A complete answer to the taste question is turned into the exact target and placement for the model, while an answer without
    // a level gives none; covers "Ít, toàn chuyến đi", "Vừa, áp dụng cho bữa 2 ngày 1", "Nhiều, áp dụng cho ngày 3" and a scope-only reply.
    /// <summary>Gives the model the values the backend already read, so a short answer is not asked about again.</summary>
    [Theory]
    [InlineData("cay", "Ít, toàn chuyến đi", "cay mức nhẹ (spicy=0.25) cho toàn chuyến", "tasteTargets")]
    [InlineData("chua", "Vừa, áp dụng cho bữa 2 ngày 1", "chua mức vừa (sour=0.5) cho bữa trưa ngày 1", "mealSlot đó")]
    [InlineData("ngọt", "Nhiều, áp dụng cho ngày 3", "ngọt mức nhiều (sweet=0.75) cho ngày 3", "mealSlot=null")]
    [InlineData("cay", "toàn chuyến", null, null)]
    public void Resolution_CompleteAnswer_NamesTargetAndPlacement(string taste, string answer, string? expected, string? field)
    {
        var context = AgentTestData.Context();
        context.History.Add(new("user", $"Tôi muốn ăn ở Đà Nẵng 2 ngày, tôi thích ăn {taste}"));
        context.State = context.State with { MealSlots = ["breakfast", "lunch", "dinner"],
            PendingQuestion = $"Bạn muốn mức {taste} như thế nào: không, nhẹ, vừa, nhiều hay rất nhiều? Bạn muốn áp dụng khẩu vị {taste} cho toàn chuyến hay ngày/bữa cụ thể nào?" };
        context.Request.Content = answer;

        var resolution = AgentTasteClarificationPolicy.Resolution(context);

        if (expected is null) resolution.Should().BeNull();
        else resolution.Should().Contain(expected).And.Contain(field!).And.Contain("không hỏi lại");
    }

    // TEST-15: Novelty wording with an unresolved meaning asks the fixed novelty question; covers "chưa ăn bao giờ", "món gì lạ lạ",
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
    public void NoveltyQuestion_NoveltyWording_AsksMeaning(string content)
    {
        var context = AgentTestData.Context(); context.Request.Content = content;

        AgentTasteClarificationPolicy.NoveltyQuestion(context).Should().Be(AgentReplyBuilder.NoveltyQuestion);
    }

    // TEST-16: Look-alike words, refused novelty and wording already meaning "different from this plan" reach the model; covers sour taste,
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
    public void NoveltyQuestion_OrdinaryOrResolvedWording_ReachesModel(string content)
    {
        var context = AgentTestData.Context(); context.Request.Content = content;

        AgentTasteClarificationPolicy.NoveltyQuestion(context).Should().BeNull();
    }

    // TEST-17: Over an existing plan, answering the novelty question with "never eaten" gets the honest no-history reply offering dishes
    // different from the plan, while "different from this plan" or a mixed answer that includes it reaches the model; covers all three.
    /// <summary>Prevents the model from inventing a never-eaten filter that ANGI has no data for.</summary>
    [Theory]
    [InlineData("Món chưa từng ăn", true)]
    [InlineData("Khác các món trong lịch hiện tại", false)]
    [InlineData("Chưa ăn bao giờ, khác lịch luôn", false)]
    public void NoveltyQuestion_AnswerToNoveltyQuestion_IsHonestAboutHistory(string content, bool noHistory)
    {
        var context = AgentTestData.Context(AgentTestData.Snapshot(AgentTestData.Item())); context.Request.Content = content;
        context.State = context.State with { PendingQuestion = AgentReplyBuilder.NoveltyQuestion };

        var reply = AgentTasteClarificationPolicy.NoveltyQuestion(context);

        if (noHistory) reply.Should().Be(AgentReplyBuilder.NoveltyWithoutHistory);
        else reply.Should().BeNull();
    }

    // TEST-18: Without any plan, the "never eaten" answer does not offer dishes "different from the current plan" but plans by taste.
    /// <summary>Avoids promising to avoid a plan that does not exist yet.</summary>
    [Fact]
    public void NoveltyQuestion_NeverEatenWithoutPlan_PlansByTaste()
    {
        var context = AgentTestData.Context(); context.Request.Content = "Món chưa từng ăn";
        context.State = context.State with { PendingQuestion = AgentReplyBuilder.NoveltyQuestion };

        AgentTasteClarificationPolicy.NoveltyQuestion(context).Should().Be(AgentReplyBuilder.NoveltyWithoutHistoryOrPlan)
            .And.Contain("Mình sẽ lập lịch theo các món hợp khẩu vị của bạn.").And.NotContain("lịch hiện tại");
    }
}
