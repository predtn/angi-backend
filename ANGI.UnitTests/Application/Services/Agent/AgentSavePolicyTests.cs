using ANGI.Application.Common.Models.Agent;
using ANGI.Application.DTOs.Agent;
using ANGI.Application.Services.Agent;
using FluentAssertions;

namespace ANGI.UnitTests.Application.Services.Agent;

/// <summary>Verifies AgentSavePolicy asks for a missing start date and saves only the exact proposal version a question was about.</summary>
public sealed class AgentSavePolicyTests
{
    private static readonly DateOnly Today = new(2026, 11, 3);
    private static readonly AgentPendingSave AskedDate = new(9, 2, AgentSavePolicy.StageDate);
    private static readonly AgentPendingSave AskedConfirm = new(9, 2, AgentSavePolicy.StageConfirm);

    // TEST-01: Saving without a start date asks for it and names the planned days; covers full and short wording and go-ahead wording.
    /// <summary>This is the missing step: "chốt lịch" used to save a roadmap with no date at all.</summary>
    [Theory]
    [InlineData("chốt lịch này")][InlineData("lưu lịch trình này")][InlineData("lưu")][InlineData("chốt đi")][InlineData("ok lên lịch")]
    public void Decide_SaveWithoutDate_AsksStartDateAndNamesDays(string content)
    {
        var decision = AgentSavePolicy.Decide(Context(content, start: null), null, Today);

        decision!.Commit.Should().BeFalse();
        decision.Reply.Should().StartWith("Lịch của bạn có 2 ngày. Bạn muốn bắt đầu từ ngày nào?").And.Contain("10/11").And.Contain("để sau");
        decision.Pending.Should().Be(AskedDate);
    }

    // TEST-02: An explicit save with a known date commits at once; covers a date in the proposal and one stated later in the conversation.
    /// <summary>Known values are never asked again.</summary>
    [Theory]
    [InlineData(true)][InlineData(false)]
    public void Decide_ExplicitSaveWithDate_Commits(bool inProposal)
    {
        var context = Context("chốt lịch này", start: inProposal ? new(2026, 12, 1) : null, conversationStart: inProposal ? null : new(2026, 12, 1));

        var decision = AgentSavePolicy.Decide(context, null, Today);

        decision!.Commit.Should().BeTrue();
        decision.StartDate.Should().BeNull();
    }

    // TEST-03: A valid answer to the date question commits with that date; covers a single date, today as the boundary and a range of the planned length.
    /// <summary>Answering the question is the instruction to save, so the user is not asked twice.</summary>
    [Theory]
    [InlineData("5/11", "2026-11-05")][InlineData("3/11", "2026-11-03")][InlineData("từ 5/11 đến 6/11", "2026-11-05")][InlineData("ngày mai", "2026-11-04")]
    public void Decide_DateAnswer_CommitsWithDate(string content, string expected)
    {
        var decision = AgentSavePolicy.Decide(Context(content, start: null), AskedDate, Today);

        decision!.Commit.Should().BeTrue();
        decision.StartDate.Should().Be(DateOnly.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
    }

    // TEST-04: A date one day in the past is refused and the question stays open.
    /// <summary>Boundary with today: a trip cannot start on a day already gone.</summary>
    [Fact]
    public void Decide_PastDate_AsksAgain()
    {
        var decision = AgentSavePolicy.Decide(Context("2/11/2026", start: null), AskedDate, Today);

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
        var decision = AgentSavePolicy.Decide(Context(content, start: null), AskedDate, Today);

        decision!.Commit.Should().BeFalse();
        decision.Reply.Should().Contain(reason).And.Contain(way);
        decision.Pending.Should().Be(AskedDate);
    }

    // TEST-06: Deferring saves with the date left open; declining saves nothing; covers both wordings per path, with and without accents.
    /// <summary>A traveler who has no date yet can still save, and a change of mind writes nothing.</summary>
    [Theory]
    [InlineData("để sau", true)][InlineData("chưa biết ngày", true)][InlineData("bo qua", true)]
    [InlineData("thôi", false)][InlineData("khoan đã", false)][InlineData("chưa lưu", false)]
    public void Decide_DateQuestion_DeferOrDecline(string content, bool commits)
    {
        var decision = AgentSavePolicy.Decide(Context(content, start: null), AskedDate, Today);

        decision!.Commit.Should().Be(commits);
        decision.StartDate.Should().BeNull();
        decision.Pending.Should().BeNull();
        if (!commits) decision.Reply.Should().Be(AgentSavePolicy.NotSaved);
    }

    // TEST-07: Unreadable date attempts and a plain "ok" repeat the question; a new request drops it; covers a bare number, an impossible date, ok and an edit.
    /// <summary>The user is neither stuck on the question nor saved by accident.</summary>
    [Theory]
    [InlineData("5", true)][InlineData("31/2", true)][InlineData("ok", true)][InlineData("lưu đi", true)]
    [InlineData("đổi bữa trưa ngày 1", false)][InlineData("đổi thành 3 ngày", false)]
    public void Decide_OtherReplies_RepeatOrRelease(string content, bool repeats)
    {
        var decision = AgentSavePolicy.Decide(Context(content, start: null), AskedDate, Today);

        if (repeats)
        {
            decision!.Commit.Should().BeFalse();
            decision.Pending.Should().Be(AskedDate);
            decision.Reply.Should().NotBeNullOrWhiteSpace();
        }
        else decision.Should().BeNull();
    }

    // TEST-08: A bare "lưu" with a date known asks once; a short yes then commits; covers every accepted yes.
    /// <summary>Step 3 of the natural-chat change: "ok" saves because the question just named this plan.</summary>
    [Theory]
    [InlineData("ok")][InlineData("Ừ")][InlineData("đúng rồi")][InlineData("có")][InlineData("được")][InlineData("lưu đi")][InlineData("ok chốt")]
    public void Decide_ShortYesAfterQuestion_Commits(string content)
    {
        var asked = AgentSavePolicy.Decide(Context("lưu", start: new(2026, 12, 1)), null, Today);
        asked!.Pending.Should().Be(AskedConfirm);
        asked.Reply.Should().StartWith("Mình lưu lịch hiện tại");

        AgentSavePolicy.Decide(Context(content, start: new(2026, 12, 1)), asked.Pending, Today)!.Commit.Should().BeTrue();
    }

    // TEST-09: A yes never saves without a matching question; covers no question, another version, another proposal, a finished proposal and a declined one.
    /// <summary>The safety rule stays: a plain yes alone authorizes nothing, and a changed plan needs a new question.</summary>
    [Theory]
    [InlineData(null, 2, 9L, "ready")][InlineData(AgentSavePolicy.StageConfirm, 1, 9L, "ready")][InlineData(AgentSavePolicy.StageConfirm, 2, 8L, "ready")][InlineData(AgentSavePolicy.StageConfirm, 2, 9L, "committed")]
    public void Decide_YesWithoutMatchingQuestion_DoesNotCommit(string? stage, int version, long proposalId, string status)
    {
        var context = Context("ok", start: new(2026, 12, 1), status: status);
        var pending = stage is null ? null : new AgentPendingSave(proposalId, version, stage);

        (AgentSavePolicy.Decide(context, pending, Today)?.Commit ?? false).Should().BeFalse();
    }

    // TEST-10: Declining the confirm question saves nothing and ends the wait.
    /// <summary>"Không" after "Mình lưu nhé?" must leave the proposal exactly as it was.</summary>
    [Fact]
    public void Decide_DeclineConfirm_DoesNotCommit()
    {
        var decision = AgentSavePolicy.Decide(Context("không", start: new(2026, 12, 1)), AskedConfirm, Today);

        decision!.Commit.Should().BeFalse();
        decision.Reply.Should().Be(AgentSavePolicy.NotSaved);
        decision.Pending.Should().BeNull();
    }

    // TEST-11: An open question or change blocks saving and no save question opens; covers a pending question, a pending change and a pending planning request.
    /// <summary>The checks of the writer stay authoritative whatever the wording.</summary>
    [Theory]
    [InlineData("question")][InlineData("change")][InlineData("planning")]
    public void Decide_OpenItem_BlocksSave(string area)
    {
        var context = Context("lưu", start: null);
        context.State = area switch
        {
            "question" => context.State with { PendingQuestion = "Bạn muốn đổi bữa nào?" },
            "change" => context.State with { PendingStateChange = new AgentState() },
            _ => context.State with { PendingPlanningRequest = new(["x"]) }
        };

        var decision = AgentSavePolicy.Decide(context, null, Today);

        decision!.Commit.Should().BeFalse();
        decision.Pending.Should().BeNull();
        decision.Reply.Should().ContainEquivalentOf("trước khi lưu");
    }

    // TEST-12: With no plan, save wording says so; a go-ahead with no plan is a planning request for the model; other text is not about saving.
    /// <summary>"Ok lên lịch" before any preview must still start planning.</summary>
    [Theory]
    [InlineData("lưu lịch trình này", true)][InlineData("lưu", true)][InlineData("ok lên lịch", false)][InlineData("gợi ý món trưa", false)]
    public void Decide_WithoutPlan_NeverCommits(string content, bool answered)
    {
        var decision = AgentSavePolicy.Decide(new AgentContextBuilderFixture(content).Context, null, Today);

        if (answered) { decision!.Commit.Should().BeFalse(); decision.Reply.Should().Be(AgentReplyBuilder.NothingToSave); }
        else decision.Should().BeNull();
    }

    // TEST-13: A saved proposal repeats its commit for an identical request but refuses when the trip has changed since; covers both.
    /// <summary>Keeps replay safe without saving a plan the user no longer described.</summary>
    [Theory]
    [InlineData(false)][InlineData(true)]
    public void Decide_CommittedProposal_ReplaysOnlyWhenUnchanged(bool changed)
    {
        var context = Context("chốt lịch này", start: new(2026, 12, 1), status: "committed");
        if (changed) context.State = context.State with { BudgetAmount = 1 };

        var decision = AgentSavePolicy.Decide(context, null, Today);

        decision!.Commit.Should().Be(!changed);
        if (changed) decision.Reply.Should().Contain("đã thay đổi");
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
