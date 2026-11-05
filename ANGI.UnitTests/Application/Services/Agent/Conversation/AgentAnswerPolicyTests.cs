using ANGI.Application.Common.Models.Agent;
using ANGI.Application.DTOs.Agent;
using ANGI.Application.Services.Agent.Conversation;
using ANGI.Application.Services.Agent.Proposals;
using FluentAssertions;

namespace ANGI.UnitTests.Application.Services.Agent.Conversation;

/// <summary>Checks that the model's reading of a reply only counts when the matching question is really open; no test here depends on any wording.</summary>
public sealed class AgentAnswerPolicyTests
{
    private static AgentProposalDto Proposal(long id = 7, int version = 2, string status = "ready") =>
        new(id, version, "create", status, new AgentSnapshot(new AgentState(), [], []), new AgentValidation([], [], 0, []), DateTime.UnixEpoch);

    private static AgentOutput Output(string? answer, string action = "answer", AgentState? state = null, string? start = null, string? end = null) =>
        new() { Action = action, Reply = "", State = state, PendingAnswer = answer, AnswerStartDate = start, AnswerEndDate = end };

    // TEST-01: A staged change comes first, then a save question about this exact proposal version, then a clarification over a ready plan; covers each and an unrelated state.
    /// <summary>Tells the model which question the message may answer, and none when nothing is open.</summary>
    [Fact]
    public void Awaited_NamesTheOpenQuestion()
    {
        // STEP-01: Prepare the save question about proposal 7 version 2.
        var save = new AgentPendingSave(7, 2, AgentConfirmationPolicy.StageConfirm);
        var date = new AgentPendingSave(7, 2, AgentConfirmationPolicy.StageDate);
        // STEP-02: Assert each open question is named.
        AgentConfirmationPolicy.Awaited(new AgentState { PendingStateChange = new AgentState() }, Proposal(), save).Should().Be(AgentConfirmationPolicy.AwaitingStateChange);
        AgentConfirmationPolicy.Awaited(new AgentState(), Proposal(), save).Should().Be(AgentConfirmationPolicy.AwaitingSave);
        AgentConfirmationPolicy.Awaited(new AgentState(), Proposal(), date).Should().Be(AgentConfirmationPolicy.AwaitingSaveDate);
        AgentConfirmationPolicy.Awaited(new AgentState { PendingQuestion = "Bữa nào?" }, Proposal(), null).Should().Be(AgentConfirmationPolicy.AwaitingQuestion);
        // STEP-03: Assert nothing is awaited with no question, for another proposal version, or without a ready plan.
        AgentConfirmationPolicy.Awaited(new AgentState(), Proposal(), null).Should().BeNull();
        AgentConfirmationPolicy.Awaited(new AgentState(), Proposal(version: 3), save).Should().BeNull();
        AgentConfirmationPolicy.Awaited(new AgentState { PendingQuestion = "Đi đâu?" }, null, null).Should().BeNull();
        AgentConfirmationPolicy.Awaited(new AgentState { PendingQuestion = "Đi đâu?", PendingPlanningRequest = new(["x"]) }, Proposal(), null).Should().BeNull();
    }

    // TEST-02: A reading counts only where it fits; covers every question against every reading.
    /// <summary>The model can never grant itself a save or a change that no question offered.</summary>
    [Theory]
    [InlineData(AgentConfirmationPolicy.AwaitingSave, "accept", "accept")]
    [InlineData(AgentConfirmationPolicy.AwaitingSave, "decline", "decline")]
    [InlineData(AgentConfirmationPolicy.AwaitingSave, "defer", null)]
    [InlineData(AgentConfirmationPolicy.AwaitingSave, "unclear", null)]
    [InlineData(AgentConfirmationPolicy.AwaitingSave, null, null)]
    [InlineData(AgentConfirmationPolicy.AwaitingSaveDate, "accept", "accept")]
    [InlineData(AgentConfirmationPolicy.AwaitingSaveDate, "decline", "decline")]
    [InlineData(AgentConfirmationPolicy.AwaitingSaveDate, "defer", "defer")]
    [InlineData(AgentConfirmationPolicy.AwaitingSaveDate, "unclear", null)]
    [InlineData(AgentConfirmationPolicy.AwaitingStateChange, "accept", "accept")]
    [InlineData(AgentConfirmationPolicy.AwaitingStateChange, "decline", "decline")]
    [InlineData(AgentConfirmationPolicy.AwaitingStateChange, "defer", null)]
    [InlineData(AgentConfirmationPolicy.AwaitingQuestion, "accept", null)]
    [InlineData(AgentConfirmationPolicy.AwaitingQuestion, "decline", "decline")]
    [InlineData(null, "accept", null)]
    [InlineData(null, "decline", null)]
    public void Read_CountsOnlyAReadingThatFitsTheQuestion(string? awaited, string? reading, string? expected)
    {
        // STEP-01: Assert AgentConfirmationPolicy.ReadAnswer()?.Kind is expected.
        AgentConfirmationPolicy.ReadAnswer(awaited, Output(reading))?.Kind.Should().Be(expected);
        if (expected is null) AgentConfirmationPolicy.ReadAnswer(awaited, Output(reading)).Should().BeNull();
    }

    // TEST-03: An output that also carries a change or uses another action is not an answer, so "ok nhưng đổi bữa trưa" can never save.
    /// <summary>Stops a message that asks for something else from being carried out as a plain yes.</summary>
    [Fact]
    public void Read_OutputCarryingAChange_IsNotAnAnswer()
    {
        // STEP-01: Assert each output with another action, a state, an item or a change is not an answer.
        AgentConfirmationPolicy.ReadAnswer(AgentConfirmationPolicy.AwaitingSave, Output("accept", "out_of_scope")).Should().BeNull();
        AgentConfirmationPolicy.ReadAnswer(AgentConfirmationPolicy.AwaitingSave, Output("accept", "patch_proposal")).Should().BeNull();
        AgentConfirmationPolicy.ReadAnswer(AgentConfirmationPolicy.AwaitingSave, Output("accept", state: new AgentState())).Should().BeNull();
        AgentConfirmationPolicy.ReadAnswer(AgentConfirmationPolicy.AwaitingSave, Output("accept") with { Items = [new("k", null, 1, "lunch", 0, 1)] }).Should().BeNull();
        AgentConfirmationPolicy.ReadAnswer(AgentConfirmationPolicy.AwaitingSave, Output("accept") with { Changes = [new("remove", "k", null)] }).Should().BeNull();
    }

    // TEST-04: Dates are kept for the start date question only, and text that is not a real date is dropped; covers a range, a save question and invalid text.
    /// <summary>A date the model read still has to be a real date before the backend checks it against today and the plan.</summary>
    [Fact]
    public void Read_DatesCountOnlyForTheDateQuestion()
    {
        // STEP-01: Prepare the date answer.
        var range = AgentConfirmationPolicy.ReadAnswer(AgentConfirmationPolicy.AwaitingSaveDate, Output("accept", start: "2026-11-14", end: "2026-11-16"))!;
        // STEP-02: Assert range holds both dates, a save question keeps none and invalid text is dropped.
        range.Start.Should().Be(new DateOnly(2026, 11, 14));
        range.End.Should().Be(new DateOnly(2026, 11, 16));
        AgentConfirmationPolicy.ReadAnswer(AgentConfirmationPolicy.AwaitingSave, Output("accept", start: "2026-11-14"))!.Start.Should().BeNull();
        AgentConfirmationPolicy.ReadAnswer(AgentConfirmationPolicy.AwaitingSaveDate, Output("accept", start: "thứ 7"))!.Start.Should().BeNull();
        AgentConfirmationPolicy.ReadAnswer(AgentConfirmationPolicy.AwaitingSaveDate, Output("accept", start: "2026-02-31"))!.Start.Should().BeNull();
    }
}
