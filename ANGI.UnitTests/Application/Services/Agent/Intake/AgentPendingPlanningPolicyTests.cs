using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Services.Agent.Constraints;
using ANGI.Application.Services.Agent.Intake;
using FluentAssertions;

namespace ANGI.UnitTests.Application.Services.Agent.Intake;

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

    // TEST-04: An amount followed by a single trip day or a duration is a total budget, not a daily one; covers ngày đầu, ngày cuối, ngày 1 and 4 ngày.
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

    // TEST-05: Many partial replies keep the original request and its parsed values while the stored message history stays bounded to 6 entries.
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

    // TEST-06: The model can neither invent a navigation focus from a taste scope nor clear a focus the backend already holds.
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

    // TEST-07: The model can neither forge a pending planning request nor clear the one the backend stored.
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

    // TEST-08: A clause that is only an amount with a unit answers the budget question; covers a daily answer, a total answer,
    // a duration followed by a daily amount and a plain đồng amount.
    /// <summary>Lets a user answer "Ngân sách bao nhiêu?" with just the amount instead of repeating the word "ngân sách".</summary>
    [Theory]
    [InlineData("500k 1 ngày", null, null, 500000d)]
    [InlineData("Khoảng 2 triệu", null, 2000000d, null)]
    [InlineData("4 ngày, 500k/ngày nhé", (short)4, null, 500000d)]
    [InlineData("1500000 đồng", null, 1500000d, null)]
    public void Capture_BareAmountAnswer_BecomesBudget(string text, short? days, double? total, double? daily)
    {
        var draft = AgentPendingPlanningPolicy.Capture(new(["Tôi thích ăn chua"]), text);

        draft.NumDays.Should().Be(days);
        draft.TotalBudget.Should().Be((decimal?)total);
        draft.DailyBudget.Should().Be((decimal?)daily);
    }

    // TEST-09: An amount inside another clause never becomes the trip budget; covers a dish price ceiling, a negated amount and a duration without money.
    /// <summary>Keeps per-dish prices and negated amounts from silently becoming a hard trip budget.</summary>
    [Theory]
    [InlineData("Tôi muốn món dưới 50k")]
    [InlineData("Không phải 500k đâu")]
    [InlineData("4 ngày")]
    public void Capture_AmountOutsideBudgetAnswer_IsIgnored(string text)
    {
        var draft = AgentPendingPlanningPolicy.Capture(new(["Tôi thích ăn chua"]), text);

        draft.TotalBudget.Should().BeNull();
        draft.DailyBudget.Should().BeNull();
    }

    // TEST-10: The combined taste question asks only the duration/budget still unknown and never the province; covers nothing known,
    // a daily budget without a duration, an unrestricted-budget request, budget and days already in state, and everything stated.
    /// <summary>Verifies which required trip values are appended to the taste question.</summary>
    [Theory]
    [InlineData("Tôi thích ăn chua", false, "Bạn cho mình biết số ngày, ngân sách cho một người (tổng chuyến hoặc mỗi ngày) để lập lịch nhé?")]
    [InlineData("Ngân sách 500k mỗi ngày, tôi thích ăn chua", false, "Bạn cho mình biết số ngày để lập lịch nhé?")]
    [InlineData("Đi 3 ngày, ngân sách không giới hạn, tôi thích ăn chua", false, null)]
    [InlineData("Tôi thích ăn chua", true, null)]
    [InlineData("Hà Nội 4 ngày, ngân sách 2 triệu, tôi thích ăn chua", false, null)]
    public void MissingTripDetails_UnknownValues_AreAskedWithoutProvince(string text, bool stateKnown, string? expected)
    {
        var state = stateKnown ? new AgentState { NumDays = 2, BudgetAmount = 500000 } : new AgentState();

        AgentPendingPlanningPolicy.MissingTripDetails(state, AgentPendingPlanningPolicy.Capture(null, text)).Should().Be(expected);
    }

    // TEST-11: Stated duration and budget override the model's reading; covers a misread daily budget, a stated total, a message
    // with no trip facts and a null state.
    /// <summary>Prevents "500k 1 ngày" from becoming a 1,500 đồng budget because the model dropped the thousands.</summary>
    [Theory]
    [InlineData("Lập lịch 3 ngày ở Hà Nội, ngân sách 500k 1 ngày", 1500, (short)2, 1500000d, (short)3)]
    [InlineData("ngân sách 2 triệu", 1500, (short)3, 2000000d, (short)3)]
    [InlineData("đổi bữa trưa ngày 2", 1500, (short)3, 1500d, (short)3)]
    public void ApplyStated_OverridesModelValues(string content, int modelBudget, short modelDays, double budget, short days)
    {
        var state = AgentPendingPlanningPolicy.ApplyStated(new AgentState { BudgetAmount = modelBudget, NumDays = modelDays }, content)!;

        state.BudgetAmount.Should().Be((decimal)budget);
        state.NumDays.Should().Be(days);
    }

    // TEST-12: A null model state stays null so an unchanged state keeps its existing shortcut.
    /// <summary>Ensures stated facts do not create a state the model did not propose.</summary>
    [Fact]
    public void ApplyStated_NullState_StaysNull()
    {
        AgentPendingPlanningPolicy.ApplyStated(null, "ngân sách 2 triệu").Should().BeNull();
    }

    // TEST-13: An answer to a trip question is read whole; covers both values in one clause, a place before the duration, a per-day amount on either side and a bare number when budget was asked.
    /// <summary>Ensures replies Capture ignores on ordinary edit messages still count once the question is about the trip.</summary>
    [Theory]
    [InlineData("3 ngày 1 triệu", true, 3, 1000000, null)]
    [InlineData("Hà Nội 3 ngày", false, 3, null, null)]
    [InlineData("500k 3 ngày", true, 3, 500000, null)]
    [InlineData("500k 1 ngày", true, null, null, 500000)]
    [InlineData("mỗi ngày 300k", true, null, null, 300000)]
    [InlineData("1 ngày 200k", true, null, null, 200000)]
    [InlineData("1500000", true, null, 1500000, null)]
    [InlineData("1.500.000", true, null, 1500000, null)]
    public void CaptureAnswer_ReadsTripReply(string content, bool budgetAsked, int? days, int? total, int? daily)
    {
        var request = AgentPendingPlanningPolicy.CaptureAnswer(null, content, budgetAsked);

        request.NumDays.Should().Be(days is null ? null : (short)days);
        request.TotalBudget.Should().Be(total);
        request.DailyBudget.Should().Be(daily);
    }

    // TEST-14: Other amounts are not a trip budget; covers a dish price cap, a meal price cap, a negated amount and a bare number nobody asked for.
    /// <summary>Protects the hard budget from price caps and from numbers that answer nothing.</summary>
    [Theory]
    [InlineData("món dưới 50k", true)]
    [InlineData("50k mỗi món", true)]
    [InlineData("không phải 500k", true)]
    [InlineData("1500000", false)]
    [InlineData("150", true)]
    public void CaptureAnswer_OtherAmounts_AreNotBudget(string content, bool budgetAsked)
    {
        var request = AgentPendingPlanningPolicy.CaptureAnswer(null, content, budgetAsked);

        request.TotalBudget.Should().BeNull();
        request.DailyBudget.Should().BeNull();
    }

    // TEST-15: A previous answer survives a later reply that repeats nothing, so answers can arrive across several turns.
    /// <summary>Ensures the second question does not lose the first answer.</summary>
    [Fact]
    public void CaptureAnswer_KeepsEarlierAnswers()
    {
        var first = AgentPendingPlanningPolicy.CaptureAnswer(null, "3 ngày", false);

        var second = AgentPendingPlanningPolicy.CaptureAnswer(first, "1 triệu", true);

        second.NumDays.Should().Be(3);
        second.TotalBudget.Should().Be(1000000);
        second.Messages.Should().Equal("3 ngày", "1 triệu");
    }

    // TEST-16: No-limit wording is recognized only as a whole answer or next to the word budget; covers both and an unrelated use of "thoải mái".
    /// <summary>Ensures a casual sentence cannot lift the budget.</summary>
    [Theory]
    [InlineData("không giới hạn", true)][InlineData("thoải mái nhé", true)][InlineData("ngân sách không giới hạn", true)][InlineData("bao nhiêu cũng được", true)]
    [InlineData("ăn thoải mái ở quán", false)][InlineData("500k", false)]
    public void StatesUnrestricted_RecognizesWholeAnswer(string content, bool expected) =>
        AgentPendingPlanningPolicy.StatesUnrestricted(content).Should().Be(expected);
}
