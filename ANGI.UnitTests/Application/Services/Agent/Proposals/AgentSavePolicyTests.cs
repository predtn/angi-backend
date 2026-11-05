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

    // TEST-01: A complete save sentence with no start date asks for it and names the planned days; a wish the model read asks the same.
    /// <summary>This is the missing step: "chốt lịch" used to save a roadmap with no date at all.</summary>
    [Theory]
    [InlineData("chốt lịch này")][InlineData("lưu lịch trình này")]
    public void Decide_SaveWithoutDate_AsksStartDateAndNamesDays(string content)
    {
        // STEP-01: Get decisions from AgentConfirmationPolicy.DecideSave and AgentConfirmationPolicy.RequestSave.
        var decision = AgentConfirmationPolicy.DecideSave(Context(content, start: null), null, Today);
        var wish = AgentConfirmationPolicy.RequestSave(Context("ưng rồi", start: null), Today);

        // STEP-02: Assert each decision.Commit is false, decision.Reply starts with the expected text and decision.Pending is AskedDate.
        foreach (var asked in new[] { decision!, wish })
        {
            asked.Commit.Should().BeFalse();
            asked.Reply.Should().StartWith("Lịch của bạn có 2 ngày. Bạn muốn bắt đầu từ ngày nào?").And.Contain("10/11").And.Contain("để sau");
            asked.Pending.Should().Be(AskedDate);
        }
    }

    // TEST-02: An explicit save with a known date commits at once; covers a date in the proposal and one stated later in the conversation.
    /// <summary>Known values are never asked again.</summary>
    [Theory]
    [InlineData(true)][InlineData(false)]
    public void Decide_ExplicitSaveWithDate_Commits(bool inProposal)
    {
        // STEP-01: Get context from Context and get decision from AgentConfirmationPolicy.DecideSave.
        var context = Context("chốt lịch này", start: inProposal ? new(2026, 12, 1) : null, conversationStart: inProposal ? null : new(2026, 12, 1));

        var decision = AgentConfirmationPolicy.DecideSave(context, null, Today);

        // STEP-02: Assert decision.Commit is true and decision.StartDate is null.
        decision!.Commit.Should().BeTrue();
        decision.StartDate.Should().BeNull();
    }

    // TEST-03: A date written in digits commits with that date without the model; covers a single date, today as the boundary and a range of the planned length.
    /// <summary>Answering the question is the instruction to save, so the user is not asked twice.</summary>
    [Theory]
    [InlineData("5/11", "2026-11-05")][InlineData("3/11", "2026-11-03")][InlineData("từ 5/11 đến 6/11", "2026-11-05")]
    public void Decide_DateAnswer_CommitsWithDate(string content, string expected)
    {
        // STEP-01: Get decision from AgentConfirmationPolicy.DecideSave.
        var decision = AgentConfirmationPolicy.DecideSave(Context(content, start: null), AskedDate, Today);

        // STEP-02: Assert decision.Commit is true and decision.StartDate is DateOnly.Parse().
        decision!.Commit.Should().BeTrue();
        decision.StartDate.Should().Be(DateOnly.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
    }

    // TEST-04: A date one day in the past is refused and the question stays open.
    /// <summary>Boundary with today: a trip cannot start on a day already gone.</summary>
    [Fact]
    public void Decide_PastDate_AsksAgain()
    {
        // STEP-01: Get decision from AgentConfirmationPolicy.DecideSave.
        var decision = AgentConfirmationPolicy.DecideSave(Context("2/11/2026", start: null), AskedDate, Today);

        // STEP-02: Assert decision.Commit is false, decision.Reply contains the expected text and decision.Pending is AskedDate.
        decision!.Commit.Should().BeFalse();
        decision.Reply.Should().Contain("đã qua");
        decision.Pending.Should().Be(AskedDate);
    }

    // TEST-05: A range whose length differs from the plan is questioned, never saved or shortened; covers one day more, one day less and a reversed range.
    /// <summary>The number of days stays a decision of the user: the reply offers keeping the plan or changing it.</summary>
    [Theory]
    [InlineData("từ 5/11 đến 7/11", "là 3 ngày", "đổi thành 3 ngày")]
    [InlineData("từ 5/11 đến 5/11", "là 1 ngày", "đổi thành 1 ngày")]
    [InlineData("7/11/2026 đến 5/11/2026", "sớm hơn", "xem lại")]
    public void Decide_RangeMismatch_AsksInsteadOfSaving(string content, string reason, string way)
    {
        // STEP-01: Get decision from AgentConfirmationPolicy.DecideSave.
        var decision = AgentConfirmationPolicy.DecideSave(Context(content, start: null), AskedDate, Today);

        // STEP-02: Assert decision.Commit is false, decision.Reply contains reason and decision.Pending is AskedDate.
        decision!.Commit.Should().BeFalse();
        decision.Reply.Should().Contain(reason).And.Contain(way);
        decision.Pending.Should().Be(AskedDate);
    }

    // TEST-06: At the date question, deferring saves with the date open and declining saves nothing; covers both readings.
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

    // TEST-07: Unreadable digits repeat the question without the model; anything else is the model's to read; covers a bare number, an impossible date, ok, a wish to save and an edit.
    /// <summary>The user is neither stuck on the question nor saved by accident.</summary>
    [Theory]
    [InlineData("5", true)][InlineData("31/2", true)][InlineData("ok", false)][InlineData("lưu đi", false)]
    [InlineData("đừng", false)][InlineData("đổi bữa trưa ngày 1", false)][InlineData("đổi thành 3 ngày", false)]
    public void Decide_OtherReplies_RepeatOrGoToModel(string content, bool repeats)
    {
        // STEP-01: Get decision from AgentConfirmationPolicy.DecideSave.
        var decision = AgentConfirmationPolicy.DecideSave(Context(content, start: null), AskedDate, Today);

        // STEP-02: Assert depending on repeats, decision.Commit is false, decision.Pending is AskedDate, decision.Reply has a value and decision is null.
        if (repeats)
        {
            decision!.Commit.Should().BeFalse();
            decision.Pending.Should().Be(AskedDate);
            decision.Reply.Should().NotBeNullOrWhiteSpace();
        }
        else decision.Should().BeNull();
    }

    // TEST-08: A wish to save with a date known asks once, and an accepted answer then commits; covers the question and the answer.
    /// <summary>A read wish never commits alone: "ok" saves because the question just named this plan.</summary>
    [Fact]
    public void RequestSave_AsksOnce_ThenAcceptedAnswerCommits()
    {
        // STEP-01: Get asked from AgentConfirmationPolicy.RequestSave.
        var asked = AgentConfirmationPolicy.RequestSave(Context("chốt đi", start: new(2026, 12, 1)), Today);
        // STEP-02: Assert asked.Commit is false, asked.Pending is AskedConfirm, asked.Reply starts with the expected text and AgentConfirmationPolicy.AnswerSave().Commit is true.
        asked.Commit.Should().BeFalse();
        asked.Pending.Should().Be(AskedConfirm);
        asked.Reply.Should().StartWith("Mình lưu lịch hiện tại");

        AgentConfirmationPolicy.AnswerSave(Context("", start: new(2026, 12, 1)), asked.Pending!, new(AgentConfirmationPolicy.Accept), Today).Commit.Should().BeTrue();
    }

    // TEST-09: A yes never reaches a save by itself; covers no question, another version, another proposal and a finished proposal.
    /// <summary>The safety rule stays: only a complete sentence is read here, and a changed plan needs a new question.</summary>
    [Theory]
    [InlineData(null, 2, 9L, "ready")][InlineData(AgentConfirmationPolicy.StageConfirm, 1, 9L, "ready")][InlineData(AgentConfirmationPolicy.StageConfirm, 2, 8L, "ready")][InlineData(AgentConfirmationPolicy.StageConfirm, 2, 9L, "committed")]
    public void Decide_YesWithoutMatchingQuestion_DoesNotCommit(string? stage, int version, long proposalId, string status)
    {
        // STEP-01: Get context from Context and derive pending from the expected value.
        var context = Context("ok", start: new(2026, 12, 1), status: status);
        var pending = stage is null ? null : new AgentPendingSave(proposalId, version, stage);

        // STEP-02: Assert the message goes to the model and no question counts as awaited for a wrong version.
        AgentConfirmationPolicy.DecideSave(context, pending, Today).Should().BeNull();
        if (stage is not null) AgentConfirmationPolicy.IsCurrent(pending, context.CurrentProposal).Should().BeFalse();
    }

    // TEST-10: Declining the confirm question saves nothing and ends the wait.
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

    // TEST-11: An open question or change blocks saving and no save question opens; covers a pending question, a pending change and a pending planning request.
    /// <summary>The checks of the writer stay authoritative whatever the wording.</summary>
    [Theory]
    [InlineData("question")][InlineData("change")][InlineData("planning")]
    public void RequestSave_OpenItem_BlocksSave(string area)
    {
        // STEP-01: Get context from Context, set context.State to a test value and get decision from AgentConfirmationPolicy.RequestSave.
        var context = Context("lưu", start: null);
        context.State = area switch
        {
            "question" => context.State with { PendingQuestion = "Bạn muốn đổi bữa nào?" },
            "change" => context.State with { PendingStateChange = new AgentState() },
            _ => context.State with { PendingPlanningRequest = new(["x"]) }
        };

        var decision = AgentConfirmationPolicy.RequestSave(context, Today);

        // STEP-02: Assert decision.Commit is false, decision.Pending is null and decision.Reply contain equivalent of the expected text.
        decision.Commit.Should().BeFalse();
        decision.Pending.Should().BeNull();
        decision.Reply.Should().ContainEquivalentOf("trước khi lưu");
    }

    // TEST-12: With no plan, a complete save sentence and a read wish both say so, and other text is the model's to read.
    /// <summary>"Ok lên lịch" before any preview must still start planning.</summary>
    [Theory]
    [InlineData("lưu lịch trình này", true)][InlineData("lưu", false)][InlineData("ok lên lịch", false)][InlineData("gợi ý món trưa", false)]
    public void Decide_WithoutPlan_NeverCommits(string content, bool answered)
    {
        // STEP-01: Get decision from AgentConfirmationPolicy.DecideSave.
        var context = new AgentContextBuilderFixture(content).Context;
        var decision = AgentConfirmationPolicy.DecideSave(context, null, Today);

        // STEP-02: Assert depending on answered, decision.Commit is false, decision.Reply is AgentReplyBuilder.NothingToSave and decision is null.
        if (answered) { decision!.Commit.Should().BeFalse(); decision.Reply.Should().Be(AgentReplyBuilder.NothingToSave); }
        else decision.Should().BeNull();
        AgentConfirmationPolicy.RequestSave(context, Today).Reply.Should().Be(AgentReplyBuilder.NothingToSave);
    }

    // TEST-13: A saved proposal repeats its commit for an identical request but refuses when the trip has changed since; covers both.
    /// <summary>Keeps replay safe without saving a plan the user no longer described.</summary>
    [Theory]
    [InlineData(false)][InlineData(true)]
    public void Decide_CommittedProposal_ReplaysOnlyWhenUnchanged(bool changed)
    {
        // STEP-01: Get context from Context, when changed, set context.State to a copy of context.State and get decision from AgentConfirmationPolicy.DecideSave.
        var context = Context("chốt lịch này", start: new(2026, 12, 1), status: "committed");
        if (changed) context.State = context.State with { BudgetAmount = 1 };

        var decision = AgentConfirmationPolicy.DecideSave(context, null, Today);

        // STEP-02: Assert decision.Commit is !changed and depending on changed, decision.Reply contains the expected text.
        decision!.Commit.Should().Be(!changed);
        if (changed) decision.Reply.Should().Contain("đã thay đổi");
    }

    // TEST-14: A date the model read goes through the same checks as one in digits; covers a good date, a past date and a range of the wrong length.
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

    // TEST-15: Agreeing at the date question without naming a day asks again instead of saving with no date.
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
