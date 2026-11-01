using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Services.Agent;
using FluentAssertions;

namespace ANGI.UnitTests.Application.Services.Agent;

/// <summary>Verifies the backend-owned pending planning request captures trip duration and budget from user text and cannot be altered by the model.</summary>
public sealed class AgentPendingPlanningPolicyTests
{
    // TEST-01: Duration and daily budget from the first request survive later taste answers and are applied without mutating the original state; covers the budget spellings 500000 đồng 1 ngày, 500.000 đồng mỗi ngày, 500k/ngày, 0,5 triệu/ngày, 500000 đồng ngày and 500k ngày.
    /// <summary>Ensures follow-up taste answers do not erase the trip size and budget from the original request.</summary>
    [Theory]
    [InlineData("500000 đồng 1 ngày")]
    [InlineData("500.000 đồng mỗi ngày")]
    [InlineData("500k/ngày")]
    [InlineData("0,5 triệu/ngày")]
    [InlineData("500000 đồng ngày")]
    [InlineData("500k ngày")]
    public void Capture_MixedRequestAndTasteAnswers_PreservesDurationAndDailyBudget(string dailyBudget)
    {
        var original = AgentTestData.State() with { NumDays = 3, BudgetAmount = 1500000 };
        var draft = AgentPendingPlanningPolicy.Capture(null, $"Tôi muốn lên lịch ăn uống Hà Nội 4 ngày, ngân sách {dailyBudget}, tôi muốn ăn chua");
        draft = AgentPendingPlanningPolicy.Capture(draft, "Nhẹ thôi");
        draft = AgentPendingPlanningPolicy.Capture(draft, "Chỉ áp dụng ngày 2");

        draft.NumDays.Should().Be(4);
        draft.DailyBudget.Should().Be(500000);
        draft.Messages.Should().HaveCount(3);
        var proposed = AgentPendingPlanningPolicy.Apply(original, draft);
        proposed.NumDays.Should().Be(4); proposed.BudgetAmount.Should().Be(2000000);
        original.NumDays.Should().Be(3); original.BudgetAmount.Should().Be(1500000);
        AgentJson.Read<AgentPendingPlanningRequest>(AgentJson.Write(draft)).NumDays.Should().Be(4);
    }

    // TEST-02: An explicit correction replaces the earlier duration and switches to a total budget without guessing a daily amount.
    /// <summary>Ensures a user correction wins over earlier values and no daily budget is derived from a total.</summary>
    [Fact]
    public void Capture_ExplicitCorrection_ReplacesEarlierValuesWithoutGuessingBudget()
    {
        var draft = AgentPendingPlanningPolicy.Capture(null, "Lập lịch 4 ngày, ngân sách 500k/ngày, tôi thích ăn chua");
        draft = AgentPendingPlanningPolicy.Capture(draft, "Đổi thành 5 ngày, tổng ngân sách 1.800.000 đồng");

        draft.NumDays.Should().Be(5); draft.TotalBudget.Should().Be(1800000); draft.DailyBudget.Should().BeNull();
        AgentPendingPlanningPolicy.Apply(AgentTestData.State(), draft).BudgetAmount.Should().Be(1800000);
    }

    // TEST-03: Negated, quoted or taste-scope text never becomes the trip duration or budget; covers a negated change, a quoted example and a taste scope of the first 2 days.
    /// <summary>Guards the parser against reading trip values from text that does not actually request them.</summary>
    [Theory]
    [InlineData("Đừng đổi sang 4 ngày, không đổi ngân sách 500k/ngày")]
    [InlineData("Ví dụ: “lập lịch 4 ngày, ngân sách 500k/ngày”")]
    [InlineData("Áp dụng chua cho 2 ngày đầu")]
    public void Capture_QuotedNegatedOrTasteScope_DoesNotBecomeTripDuration(string text)
    {
        var draft = AgentPendingPlanningPolicy.Capture(null, text);
        draft.NumDays.Should().BeNull(); draft.DailyBudget.Should().BeNull(); draft.TotalBudget.Should().BeNull();
        draft.Messages.Should().ContainSingle().Which.Should().Be(text);
    }

    // TEST-07: An amount followed by a single trip day or a duration is a total budget, not a daily one; covers ngày đầu, ngày cuối, ngày 1 and 4 ngày.
    /// <summary>Keeps "ngày" that names one day of the trip or its length from turning a total budget into a per-day budget.</summary>
    [Theory]
    [InlineData("ngân sách 500k ngày đầu, sau đó tính tiếp")]
    [InlineData("ngân sách 500k ngày cuối thôi")]
    [InlineData("ngân sách 500k ngày 1, còn lại tùy bạn")]
    [InlineData("ngân sách 500000 đồng 4 ngày")]
    public void Capture_AmountBeforeTripDayOrDuration_IsTotalBudget(string text)
    {
        var draft = AgentPendingPlanningPolicy.Capture(null, text);
        draft.TotalBudget.Should().Be(500000); draft.DailyBudget.Should().BeNull();
    }

    // TEST-04: Many partial replies keep the original request and its parsed values while the stored message history stays bounded to 6 entries.
    /// <summary>Ensures long clarification exchanges keep the original request while stored history stays bounded.</summary>
    [Fact]
    public void Capture_ManyPartialReplies_KeepsOriginalRequestAndBoundsMemory()
    {
        const string original = "Lập lịch 4 ngày ở Hà Nội, ngân sách 500k/ngày, tôi thích ăn chua";
        var draft = AgentPendingPlanningPolicy.Capture(null, original);
        for (var index = 0; index < 10; index++) draft = AgentPendingPlanningPolicy.Capture(draft, $"Câu trả lời bổ sung {index}");
        draft.Messages.Should().HaveCount(6).And.Contain(original).And.Contain("Câu trả lời bổ sung 9");
        draft.NumDays.Should().Be(4); draft.DailyBudget.Should().Be(500000);
    }

    // TEST-05: The model can neither invent a navigation focus from a taste scope nor clear a focus the backend already holds.
    /// <summary>Keeps navigation focus under backend control so model output cannot move or reset it.</summary>
    [Fact]
    public void Accept_ModelTasteScopeCannotInventOrClearNavigationFocus()
    {
        var policy = new AgentStatePolicy();
        var state = AgentTestData.State();
        policy.Accept(state, state with { FocusDay = 2 }).FocusDay.Should().BeNull();
        var focused = state with { FocusDay = 2, FocusMeal = "lunch" };
        var accepted = policy.Accept(focused, state);
        accepted.FocusDay.Should().Be(2); accepted.FocusMeal.Should().Be("lunch");
    }

    // TEST-06: The model can neither forge a pending planning request nor clear the one the backend stored.
    /// <summary>Keeps the pending planning request under backend control so model output cannot forge or drop it.</summary>
    [Fact]
    public void Accept_ModelCannotForgeOrClearBackendPendingRequest()
    {
        var state = AgentTestData.State() with { NumDays = 3, BudgetAmount = 1500000 };
        var forged = new AgentPendingPlanningRequest(["forged"], 30, DailyBudget: 999999);
        var policy = new AgentStatePolicy();
        policy.Accept(state, state with { PendingPlanningRequest = forged }).PendingPlanningRequest.Should().BeNull();
        var pending = state with { PendingPlanningRequest = new(["Lập lịch 3 ngày"], 3) };
        policy.Accept(pending, state).PendingPlanningRequest.Should().BeSameAs(pending.PendingPlanningRequest);
    }
}
