using ANGI.Application.Common.Models.Agent;
using ANGI.Application.DTOs.Agent;
using ANGI.Application.Services.Agent.Conversation;
using ANGI.Application.Services.Agent.Proposals;
using FluentAssertions;

namespace ANGI.UnitTests.Application.Services.Agent.Proposals;

/// <summary>Verifies AgentConfirmationPolicy asks for a missing start date and saves only the exact proposal version a question was about.</summary>
public sealed class AgentSavePolicyTests
{
    private static readonly DateOnly Today = new(2026, 11, 3);
    private static readonly AgentPendingSave AskedDate = new(9, 2, AgentConfirmationPolicy.StageDate);
    private static readonly AgentPendingSave AskedConfirm = new(9, 2, AgentConfirmationPolicy.StageConfirm);

    // TEST-01: At the date question, deferring saves with the date open and declining saves nothing; covers both readings.
    /// <summary>A traveler who has no date yet can still save, and a change of mind writes nothing.</summary>
    [Theory]
    [InlineData(AgentConfirmationPolicy.Defer, true)][InlineData(AgentConfirmationPolicy.Decline, false)]
    public void Answer_DateQuestion_DeferOrDecline(string kind, bool commits)
    {
        // STEP-01: Get decision from AgentConfirmationPolicy.AnswerSave.
        var decision = AgentConfirmationPolicy.AnswerSave(Context("", start: null), AskedDate, new(kind), Today);

        // STEP-02: Assert decision.Commit is commits, decision.StartDate is null, decision.Pending is null and 1 more.
        decision.Commit.Should().Be(commits);
        decision.StartDate.Should().BeNull();
        decision.Pending.Should().BeNull();
        if (!commits) decision.Reply.Should().Be(AgentConfirmationPolicy.NotSaved);
    }

    // TEST-02: Declining the confirm question saves nothing and ends the wait.
    /// <summary>"Không" after "Mình lưu nhé?" must leave the proposal exactly as it was.</summary>
    [Fact]
    public void Answer_DeclineConfirm_DoesNotCommit()
    {
        // STEP-01: Get decision from AgentConfirmationPolicy.AnswerSave.
        var decision = AgentConfirmationPolicy.AnswerSave(Context("", start: new(2026, 12, 1)), AskedConfirm, new(AgentConfirmationPolicy.Decline), Today);

        // STEP-02: Assert decision.Commit is false, decision.Reply is AgentConfirmationPolicy.NotSaved and decision.Pending is null.
        decision.Commit.Should().BeFalse();
        decision.Reply.Should().Be(AgentConfirmationPolicy.NotSaved);
        decision.Pending.Should().BeNull();
    }

    // TEST-03: A date the model read goes through the same checks as one in digits; covers a good date, a past date and a range of the wrong length.
    /// <summary>The model reads "thứ 7 tuần sau"; whether that day can be used stays the backend's decision.</summary>
    [Theory]
    [InlineData("2026-11-07", null, true)][InlineData("2026-11-02", null, false)][InlineData("2026-11-05", "2026-11-09", false)]
    public void Answer_DateTheModelRead_IsCheckedLikeAnyOther(string start, string? end, bool commits)
    {
        // STEP-01: Get decision from AgentConfirmationPolicy.AnswerSave.
        var read = new AgentAnswer(AgentConfirmationPolicy.Accept, DateOnly.Parse(start, System.Globalization.CultureInfo.InvariantCulture), end is null ? null : DateOnly.Parse(end, System.Globalization.CultureInfo.InvariantCulture));
        var decision = AgentConfirmationPolicy.AnswerSave(Context("", start: null), AskedDate, read, Today);

        // STEP-02: Assert decision.Commit is commits, decision.Pending keeps the question when refused and decision.StartDate matches when saved.
        decision.Commit.Should().Be(commits);
        if (commits) decision.StartDate.Should().Be(read.Start);
        else decision.Pending.Should().Be(AskedDate);
    }

    // TEST-04: Agreeing at the date question without naming a day asks again instead of saving with no date.
    /// <summary>"Ok, lưu đi" does not answer which day.</summary>
    [Fact]
    public void Answer_AcceptWithoutDate_AsksDateAgain()
    {
        // STEP-01: Get decision from AgentConfirmationPolicy.AnswerSave.
        var decision = AgentConfirmationPolicy.AnswerSave(Context("", start: null), AskedDate, new(AgentConfirmationPolicy.Accept), Today);

        // STEP-02: Assert decision.Commit is false, decision.Pending is AskedDate and decision.Reply has the expected text.
        decision.Commit.Should().BeFalse();
        decision.Pending.Should().Be(AskedDate);
        decision.Reply.Should().Contain("Bạn muốn bắt đầu từ ngày nào");
    }

    /// <summary>Builds a turn over proposal 9 version 2 with the given message and start dates.</summary>
    private static AgentContext Context(string content, DateOnly? start, string status = "ready", DateOnly? conversationStart = null)
    {
        var snapshot = AgentTestData.Snapshot(AgentTestData.Item());
        snapshot = snapshot with { State = snapshot.State with { StartDate = start } };
        return new AgentContext
        {
            ConversationId = 7, UserId = 3, State = snapshot.State with { StartDate = conversationStart ?? start }, Source = snapshot,
            Request = new SendAgentMessageRequestDto { ClientMessageId = 1, ExpectedConversationVersion = 1, Content = content },
            CurrentProposal = new AgentProposalDto(9, 2, "create", status, snapshot, new AgentValidation([], [], 0, []), DateTime.UtcNow.AddHours(1))
        };
    }

    /// <summary>Holds a turn with no proposal at all.</summary>
    private sealed class AgentContextBuilderFixture(string content)
    {
        public AgentContext Context { get; } = new()
        {
            ConversationId = 7, UserId = 3, State = new AgentState(),
            Request = new SendAgentMessageRequestDto { ClientMessageId = 1, ExpectedConversationVersion = 1, Content = content }
        };
    }
}
