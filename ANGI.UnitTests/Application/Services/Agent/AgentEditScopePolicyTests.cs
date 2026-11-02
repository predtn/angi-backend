using ANGI.Application.Services.Agent;
using FluentAssertions;

namespace ANGI.UnitTests.Application.Services.Agent;

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

        AgentEditScopePolicy.Question(context).Should().Be("Bạn muốn thay đổi toàn lịch hay ngày/bữa hoặc món cụ thể nào?");
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
}
