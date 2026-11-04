using FluentAssertions;
using ANGI.Application.Services.Agent.Constraints;

namespace ANGI.UnitTests.Application.Services.Agent.Constraints;

/// <summary>Verifies edits that name nothing are clarified by the backend while every edit with a real target still reaches the model.</summary>
public sealed class AgentEditScopePolicyTests
{
    // TEST-01: An edit made only of an edit verb, fillers and pointing words asks which item to change; covers "đổi cái đó đi",
    // "Thay món khác đi", "bỏ nó đi nhé" and "sửa cái này giúp tôi".
    /// <summary>Ensures pointing words without an antecedent are never guessed as a whole-plan rewrite.</summary>
    [Theory]
    [InlineData("đổi cái đó đi")]
    [InlineData("Thay món khác đi")]
    [InlineData("bỏ nó đi nhé")]
    [InlineData("sửa cái này giúp tôi")]
    public void Question_EditWithoutTarget_AsksWhichItem(string content)
    {
        var context = AgentTestData.Context(AgentTestData.Snapshot(AgentTestData.Item()));
        context.Request.Content = content;

        AgentEditScopePolicy.Question(context).Should().StartWith("Bạn muốn đổi món nào, hay cả lịch?");
    }

    // TEST-02: An edit with a day, meal, whole-plan wording, a new requirement or a non-edit message reaches the model; covers
    // "Đổi món khác cho bữa 1 ngày 1", "Đổi lại toàn bộ chuyến đi", "đổi sang món chay", "Thay Phở Gà" and "cái đó ngon không".
    /// <summary>Keeps the rule narrow so real edit requests are never blocked by an unnecessary question.</summary>
    [Theory]
    [InlineData("Đổi món khác cho bữa 1 ngày 1")]
    [InlineData("Đổi lại toàn bộ chuyến đi")]
    [InlineData("đổi sang món chay")]
    [InlineData("Thay Phở Gà")]
    [InlineData("cái đó ngon không")]
    public void Question_EditWithTarget_ReachesModel(string content)
    {
        var context = AgentTestData.Context(AgentTestData.Snapshot(AgentTestData.Item()));
        context.Request.Content = content;

        AgentEditScopePolicy.Question(context).Should().BeNull();
    }

    // TEST-03: Without a plan, or with an item selected in the UI or a focused day, the rule does not apply; covers no plan, a selected
    // item key and a focused day.
    /// <summary>Lets the existing selection and focus mechanisms resolve "cái đó" instead of asking again.</summary>
    [Theory]
    [InlineData("no-plan")][InlineData("selected")][InlineData("focus")]
    public void Question_PlanlessOrSelected_ReachesModel(string area)
    {
        var context = AgentTestData.Context(area == "no-plan" ? null : AgentTestData.Snapshot(AgentTestData.Item()));
        context.Request.Content = "đổi cái đó đi";
        if (area == "selected") context.Request.SelectedItemKey = "a";
        if (area == "focus") context.State = context.State with { FocusDay = 1 };

        AgentEditScopePolicy.Question(context).Should().BeNull();
    }

    // TEST-04: A dislike without a target asks which item to change; covers "không ngon", "đắt quá" and a polite form.
    /// <summary>Prevents the model from searching the catalog for "cái này" and reporting a dish the user never named as missing.</summary>
    [Theory]
    [InlineData("cái này không ngon")]
    [InlineData("món này đắt quá")]
    [InlineData("tôi không thích món đó")]
    public void Question_DislikeWithoutTarget_AsksWhichItem(string content)
    {
        var context = AgentTestData.Context(AgentTestData.Snapshot(AgentTestData.Item()));
        context.Request.Content = content;

        AgentEditScopePolicy.Question(context).Should().StartWith("Bạn muốn đổi món nào, hay cả lịch?");
    }

    // TEST-05: A dislike naming something, or a compliment, reaches the model; covers a named dish, a day and praise.
    /// <summary>Keeps the rule from catching messages that already carry a target or no complaint.</summary>
    [Theory]
    [InlineData("Phở Gà không ngon")]
    [InlineData("ngày 2 không ngon")]
    [InlineData("cái này ngon")]
    public void Question_DislikeWithTarget_ReachesModel(string content)
    {
        var context = AgentTestData.Context(AgentTestData.Snapshot(AgentTestData.Item()));
        context.Request.Content = content;

        AgentEditScopePolicy.Question(context).Should().BeNull();
    }

    // TEST-06: Pointing words are not catalog names; covers "cái này", "món đó", a real dish and empty text.
    /// <summary>Stops a lookup that could only report a false "not found".</summary>
    [Theory]
    [InlineData("cái này", true)]
    [InlineData("món đó", true)]
    [InlineData("Phở Gà", false)]
    [InlineData("  ", false)]
    public void IsPointingOnly_ReadsWords(string name, bool expected)
    {
        AgentEditScopePolicy.IsPointingOnly(name).Should().Be(expected);
    }

    // TEST-07: A bare acknowledgement is recognized only over a plan with nothing open; covers acknowledgements, a real request, no plan,
    // an open question, a pending change, a pending planning request and a selected item.
    /// <summary>Saves the two to three LLM calls such a message used to spend on a proposal that was then rejected.</summary>
    [Theory]
    [InlineData("ok", "", true)][InlineData("Oke nhé", "", true)][InlineData("thôi", "", true)][InlineData("hmm", "", true)][InlineData("được rồi", "", true)]
    [InlineData("ok đổi bữa trưa", "", false)][InlineData("ok", "no-plan", false)][InlineData("ok", "question", false)]
    [InlineData("ok", "change", false)][InlineData("ok", "planning", false)][InlineData("ok", "selected", false)]
    public void IsBareAcknowledgement_OnlyWithNothingOpen(string content, string area, bool expected)
    {
        var context = AgentTestData.Context(area == "no-plan" ? null : AgentTestData.Snapshot(AgentTestData.Item()));
        context.Request.Content = content;
        if (area == "question") context.State = context.State with { PendingQuestion = "Có muốn đổi không?" };
        if (area == "change") context.State = context.State with { PendingStateChange = AgentTestData.State() };
        if (area == "planning") context.State = context.State with { PendingPlanningRequest = new(["x"]) };
        if (area == "selected") context.Request.SelectedItemKey = "a";

        AgentEditScopePolicy.IsBareAcknowledgement(context).Should().Be(expected);
    }
}
