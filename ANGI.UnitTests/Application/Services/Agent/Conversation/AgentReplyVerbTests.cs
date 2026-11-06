using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Services.Agent.Conversation;
using FluentAssertions;

namespace ANGI.UnitTests.Application.Services.Agent.Conversation;

/// <summary>Verifies the words a model writes before the changes slot never repeat the verb the slot already starts with.</summary>
public sealed class AgentReplyVerbTests
{
    // TEST-01: A removal's verb is written once; covers the verb inside a sentence, the verb opening it, and another word before the slot.
    /// <summary>Conversation 193 showed "Mình bỏ bỏ Bánh Tráng", because the slot already starts with "bỏ".</summary>
    [Theory]
    [InlineData("Mình bỏ {changes} nhé, tổng giờ khoảng {total}. Bạn xem lại nhé.", "Mình bỏ Bánh Tráng")]
    [InlineData("Bỏ {changes} nhé, tổng giờ khoảng {total}. Bạn xem lại nhé.", "Bỏ Bánh Tráng")]
    [InlineData("Mình sửa {changes} nhé, tổng giờ khoảng {total}. Bạn xem lại nhé.", "Mình sửa bỏ Bánh Tráng")]
    public void WordedSummary_VerbBeforeChanges_IsNotRepeated(string template, string expected)
    {
        // STEP-01: Get a plan of two dishes and the same plan without the second one.
        var kept = AgentTestData.Item() with { DishName = "Phở Gà" };
        var removed = AgentTestData.Item(2, "b", 1, "dinner") with { DishName = "Bánh Tráng" };
        var source = AgentTestData.Snapshot(kept, removed);
        var snapshot = source with { Items = [kept] };

        // STEP-02: Get the sentence from AgentReplyBuilder.WordedSummary.
        var reply = AgentReplyBuilder.WordedSummary(template, snapshot, source, AgentTestData.Context(source));

        // STEP-03: Assert the sentence starts as expected and never doubles the verb.
        reply.Should().StartWith(expected);
        reply.Should().NotContain("bỏ bỏ");
    }
}
