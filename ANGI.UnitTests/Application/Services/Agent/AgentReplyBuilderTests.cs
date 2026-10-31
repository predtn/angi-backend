using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Services.Agent;
using FluentAssertions;

namespace ANGI.UnitTests.Application.Services.Agent;

/// <summary>Checks honest user-facing taste guidance and fallback warnings tied to the selected items.</summary>
public sealed class AgentReplyBuilderTests
{
    [Fact]
    public async Task FreshRecoItems_DoNotInheritAnOldFallbackWarning()
    {
        var requestId = Guid.NewGuid();
        var state = AgentTestData.State() with { TasteTargets = [new("spicy", .75)] };
        var item = AgentTestData.Item() with { RequestId = requestId, Price = 50000 };
        var snapshot = new AgentSnapshot(state, [item],
            [new(null, null, [1], null, state), new(null, null, [1], requestId, state)]);
        var validator = AgentTestData.Validator(AgentTestData.Dish()).Validator;
        var validated = await validator.ValidateAsync(snapshot, null, true, false, default);
        validated.Validation.Errors.Should().BeEmpty();
        validated.Validation.Warnings.Should().NotContain(warning => warning.Contains("đánh giá"));
        var reply = AgentReplyBuilder.Proposal(snapshot);
        reply.Should().NotContain("thứ tự đánh giá");
        reply.Should().Contain("ưu tiên mềm").And.Contain("chưa bảo đảm từng món").And.Contain("lịch sử tương tác");
    }

    [Fact]
    public void SelectedFallbackItem_IsStillDisclosed()
    {
        var snapshot = AgentTestData.Snapshot(AgentTestData.Item());
        AgentReplyBuilder.Proposal(snapshot).Should().Contain("thứ tự đánh giá thay cho cá nhân hóa");
    }

    [Fact]
    public void MealTasteOverride_AlsoDisclosesThatTasteIsNotGuaranteed()
    {
        var snapshot = AgentTestData.Snapshot(AgentTestData.Item());
        snapshot = snapshot with { State = snapshot.State with { TasteOverrides = [new(1, "lunch", [new("spicy", .5)])] } };
        AgentReplyBuilder.Proposal(snapshot).Should().Contain("chưa bảo đảm từng món");
    }
}
