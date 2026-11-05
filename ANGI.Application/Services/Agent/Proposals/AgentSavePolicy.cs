using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Services.Agent.Conversation;
using ANGI.Application.UseCases.Agent;

namespace ANGI.Application.Services.Agent.Proposals;

/// <summary>What a save turn does: answer and possibly keep waiting, or commit the current proposal with an optional start date.</summary>
public sealed record AgentSaveDecision(string? Reply, AgentPendingSave? Pending = null, bool Commit = false, DateOnly? StartDate = null);

/// <summary>
/// Runs the save conversation without the model: asks for a missing start date, reads the answer, and lets a short "ok" save
/// only the exact proposal version the question was about. Any other message simply ends the waiting.
/// </summary>
public static class AgentSavePolicy
{
    public const string StageDate = "date";
    public const string StageConfirm = "confirm";
    public const string NotSaved = "Mình chưa lưu nhé. Ưng rồi thì cứ bảo mình chốt.";

    /// <summary>Returns the save step for this message, or null when it is not about saving and should go to the model.</summary>
    public static AgentSaveDecision? Decide(AgentContext context, AgentPendingSave? pending, DateOnly today)
    {
        // STEP-01: Prepare content and proposal.
        var content = context.Request.Content;
        var proposal = context.CurrentProposal;
        // STEP-02: When pending exists, proposal has the expected shape, proposal.ProposalId is pending.ProposalId and 1 more condition(s) and more.
        if (pending is not null && proposal is { Status: "ready" } && proposal.ProposalId == pending.ProposalId && proposal.Version == pending.ProposalVersion)
        {
            if (AgentChatPolicy.DeclinesSave(content)) return new(NotSaved);
            if (pending.Stage == StageConfirm)
            {
                if (AgentChatPolicy.AcceptsSave(content)) return Save(context, today);
            }
            else
            {
                if (AgentChatPolicy.DefersStartDate(content)) return new(null, Commit: true);
                // "Ok, lưu đi" chưa trả lời câu hỏi ngày nào: hỏi lại thay vì coi là yêu cầu mới.
                if (AgentChatPolicy.AcceptsSave(content)) return AskDate(context, today);
                if (AgentDatePolicy.Read(content, today) is { } answer) return FromAnswer(context, pending, answer, today);
                if (AgentDatePolicy.LooksLikeDate(content))
                    return new("Mình chưa đọc được ngày này. Bạn nhập ngày và tháng giúp mình, ví dụ " + Short(today.AddDays(7)) + " nhé.", pending);
            }
        }
        // STEP-03: Return the result of Save early when AgentChatPolicy.IsSaveRequest(content) holds.
        if (AgentChatPolicy.IsSaveRequest(content)) return Save(context, today);
        // STEP-04: Prepare ready.
        var ready = proposal?.Status == "ready";
        // STEP-05: Return null early when AgentChatPolicy.IsShortSaveRequest(content) does not hold and ready && AgentChatPolicy.IsGoAheadRequest(content) is false.
        if (!AgentChatPolicy.IsShortSaveRequest(content) && !(ready && AgentChatPolicy.IsGoAheadRequest(content))) return null;
        // STEP-06: Return the new result early when ready is false.
        if (!ready) return new(AgentReplyBuilder.SaveConfirmation(proposal));
        // STEP-07: Return the new result early when HasOpenItem(context.State) holds.
        if (HasOpenItem(context.State)) return new(AgentChatPolicy.SaveBlocked(context.State));
        // STEP-08: Return one of two results depending on StartDate() is missing.
        return StartDate(context) is null ? AskDate(context, today)
            : new(AgentReplyBuilder.SaveConfirmation(proposal), new(proposal!.ProposalId, proposal.Version, StageConfirm));
    }

    /// <summary>An explicit instruction to save: commits a ready proposal once its start date is known, otherwise says what is missing.</summary>
    private static AgentSaveDecision Save(AgentContext context, DateOnly today)
    {
        // STEP-01: Prepare proposal.
        var proposal = context.CurrentProposal;
        // STEP-02: Return the new result early when HasOpenItem(context.State) holds.
        if (HasOpenItem(context.State)) return new(AgentChatPolicy.SaveBlocked(context.State));
        // STEP-03: Return the new result early when proposal is missing or proposal.Status is not ("ready" or "committed").
        if (proposal is null || proposal.Status is not ("ready" or "committed")) return new(AgentReplyBuilder.NothingToSave);
        // STEP-04: Return one of two results depending on AgentConversationRules.SameConstraints(context.State, proposal.Snapshot.State) holds early when proposal.Status is "committed".
        if (proposal.Status == "committed")
            return AgentConversationRules.SameConstraints(context.State, proposal.Snapshot.State) ? new(null, Commit: true)
                : new("Yêu cầu chuyến đi đã thay đổi nhưng mình chưa có lịch mới để lưu. Bạn nhờ mình lập lại lịch trước khi chốt nhé.");
        // STEP-05: Return one of two results depending on StartDate() is missing.
        return StartDate(context) is null ? AskDate(context, today) : new(null, Commit: true);
    }

    /// <summary>Accepts a date only when it is not past and a stated range matches the planned days; the plan itself is never changed here.</summary>
    private static AgentSaveDecision FromAnswer(AgentContext context, AgentPendingSave pending, AgentDateAnswer answer, DateOnly today)
    {
        // STEP-01: Return the new result early when answer.Start < today.
        if (answer.Start < today) return new($"Ngày {Short(answer.Start)} đã qua rồi. Bạn chọn ngày khác giúp mình nhé.", pending);
        // STEP-02: Prepare days.
        var days = context.CurrentProposal!.Snapshot.State.NumDays ?? 1;
        // STEP-03: When answer.End exists, prepare stated, return the new result early when stated < 1 and 1 more action(s).
        if (answer.End is { } end)
        {
            var stated = end.DayNumber - answer.Start.DayNumber + 1;
            if (stated < 1) return new("Ngày kết thúc đang sớm hơn ngày bắt đầu. Bạn xem lại giúp mình nhé.", pending);
            if (stated != days)
                return new($"Từ {Short(answer.Start)} đến {Short(end)} là {stated} ngày, nhưng lịch hiện có {days} ngày. " +
                    $"Bạn muốn đi từ {Short(answer.Start)} đến {Short(answer.Start.AddDays(days - 1))}, hay nhắn “đổi thành {stated} ngày” để mình lập lại lịch?", pending);
        }
        // STEP-04: Return the new result.
        return new(null, Commit: true, StartDate: answer.Start);
    }

    /// <summary>Names the planned days so the user sees both values they are settling, with an example built from real upcoming dates.</summary>
    private static AgentSaveDecision AskDate(AgentContext context, DateOnly today)
    {
        // STEP-01: Prepare proposal, days, example through today.AddDays and sample from Short.
        var proposal = context.CurrentProposal!;
        var days = proposal.Snapshot.State.NumDays ?? 1;
        var example = today.AddDays(7);
        var sample = days > 1 ? $"“{Short(example)}” hoặc “từ {Short(example)} đến {Short(example.AddDays(days - 1))}”" : $"“{Short(example)}”";
        // STEP-02: Return the new result.
        return new($"Lịch của bạn có {days} ngày. Bạn muốn bắt đầu từ ngày nào? Ví dụ {sample}. Chưa biết ngày đi thì nhắn “để sau” nhé.",
            new(proposal.ProposalId, proposal.Version, StageDate));
    }

    /// <summary>The start date already agreed: from the conversation first, because the user may have stated it after the preview was built.</summary>
    private static DateOnly? StartDate(AgentContext context) => context.State.StartDate ?? context.CurrentProposal?.Snapshot.State.StartDate;

    private static bool HasOpenItem(AgentState state) => state.PendingQuestion is not null || state.PendingStateChange is not null || state.PendingPlanningRequest is not null;

    private static string Short(DateOnly date) => $"{date.Day}/{date.Month}";
}
