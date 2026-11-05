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
    // TEST-09: Only a declared taste preference counts as taste wording; covers a declaration, an edit request and a negated preference.
    /// <summary>Limits meal-scope narrowing evidence to messages that actually state a taste.</summary>
    [Theory]
    [InlineData("Tôi thích ăn chua vừa cho bữa 2 ngày 1", true)]
    [InlineData("Đổi món khác cho bữa 1 ngày 1", false)]
    [InlineData("Tôi không thích ăn cay", false)]
    public void StatesTaste_OnlyForDeclaredPreferences(string content, bool expected)
    {
        // STEP-01: Assert AgentTasteClarificationPolicy.StatesTaste() is expected.
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
        // STEP-01: Assert AgentTasteClarificationPolicy.StatesTaste() is expected.
        AgentTasteClarificationPolicy.StatesTaste(content).Should().Be(expected);
    }

    // TEST-11: Mentioned tastes are listed by display name once each and look-alike words are ignored; covers two tastes and a "chưa" message.
    /// <summary>Supplies the taste names used by the backend-rendered taste questions.</summary>
    [Theory]
    [InlineData("Đừng cay quá, ít ngọt thôi, cay vừa", new[] { "cay", "ngọt" })]
    [InlineData("Món này chưa ngon", new string[0])]
    public void MentionedTastes_NamesTastesIgnoringLookAlikes(string content, string[] expected)
    {
        // STEP-01: Assert AgentTasteClarificationPolicy.MentionedTastes() equals expected.
        AgentTasteClarificationPolicy.MentionedTastes(content).Should().Equal(expected);
    }

}
