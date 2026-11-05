using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.DTOs.Agent;
using ANGI.Application.UseCases.Agent;
using ANGI.Application.UseCases.Validators.Agent;

namespace ANGI.Application.Services.Agent.Conversation;

/// <summary>What the model read in a reply to the question the backend is waiting on, with the trip dates it named.</summary>
public sealed record AgentAnswer(string Kind, DateOnly? Start = null, DateOnly? End = null);

/// <summary>A start date the user gave for a trip, with the end date when they named a range.</summary>
public sealed record AgentDateAnswer(DateOnly Start, DateOnly? End);

/// <summary>What a save turn does: answer and possibly keep waiting, or commit the current proposal with an optional start date.</summary>
public sealed record AgentSaveDecision(string? Reply, AgentPendingSave? Pending = null, bool Commit = false, DateOnly? StartDate = null);

/// <summary>
/// Holds every question the backend waits on and what may follow it: which question is open, whether the model's reading of the reply fits it, the steps of saving
/// and a staged change of conditions. The model reads how the user answers; only a complete save sentence, plain draft controls and a date written in digits are recognized here, because none of them depends on how people talk.
/// </summary>
public static class AgentConfirmationPolicy
{
    // Open questions

    public const string AwaitingSave = "save";
    public const string AwaitingSaveDate = "save_date";
    public const string AwaitingStateChange = "state_change";
    public const string AwaitingQuestion = "question";
    public const string Accept = "accept";
    public const string Decline = "decline";
    public const string Defer = "defer";

    /// <summary>The question this message may answer, or null when nothing waits; a save question counts only for the exact proposal version it asked about.</summary>
    public static string? Awaited(AgentState state, AgentProposalDto? proposal, AgentPendingSave? pending)
    {
        // STEP-01: Return StateChange early when a constraint change waits for consent.
        if (state.PendingStateChange is not null) return AwaitingStateChange;
        // STEP-02: Return the save question early when it is about the current ready proposal.
        if (IsCurrent(pending, proposal)) return pending!.Stage == StageConfirm ? AwaitingSave : AwaitingSaveDate;
        // STEP-03: Return Question when a clarification stands over a ready plan, otherwise null.
        return state.PendingQuestion is not null && state.PendingPlanningRequest is null && proposal?.Status == "ready" ? AwaitingQuestion : null;
    }

    /// <summary>True when the save question names this exact proposal version, so an answer can never save a different plan.</summary>
    public static bool IsCurrent(AgentPendingSave? pending, AgentProposalDto? proposal) => pending is not null && proposal is { Status: "ready" } &&
        proposal.ProposalId == pending.ProposalId && proposal.Version == pending.ProposalVersion;

    /// <summary>The model's reading when it answers the open question and nothing else; anything carrying a change or fitting no open question is not an answer.</summary>
    public static AgentAnswer? ReadAnswer(string? awaited, AgentOutput output)
    {
        // STEP-01: Return null early when nothing waits or the output also asks for something else.
        if (awaited is null || output is not { Action: "answer", State: null, Items.Length: 0, Changes.Length: 0 }) return null;
        // STEP-02: Return the reading that fits the question, with the dates only when a start date is being asked for.
        return (awaited, output.PendingAnswer) switch
        {
            (_, Decline) => new(Decline),
            (AwaitingStateChange or AwaitingSave, Accept) => new(Accept),
            (AwaitingSaveDate, Defer) => new(Defer),
            (AwaitingSaveDate, Accept) => new(Accept, Date(output.AnswerStartDate), Date(output.AnswerEndDate)),
            _ => null
        };
    }

    private static DateOnly? Date(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

    // Saving

    public const string StageDate = "date";
    public const string StageConfirm = "confirm";
    public const string NotSaved = "Mình chưa lưu nhé. Ưng rồi thì cứ bảo mình chốt.";

    /// <summary>Returns the save step for a message the backend can recognize without the model, or null when the model should read it.</summary>
    public static AgentSaveDecision? DecideSave(AgentContext context, AgentPendingSave? pending, DateOnly today)
    {
        // STEP-01: Prepare content.
        var content = context.Request.Content;
        // STEP-02: When a start date is being asked for, use a date written in digits, or ask again when it cannot be read.
        if (IsCurrent(pending, context.CurrentProposal) && pending!.Stage == StageDate)
        {
            if (ReadDate(content, today) is { } answer) return FromAnswer(context, pending, answer, today);
            if (LooksLikeDate(content))
                return new("Mình chưa đọc được ngày này. Bạn nhập ngày và tháng giúp mình, ví dụ " + Short(today.AddDays(7)) + " nhé.", pending);
        }
        // STEP-03: Return the result of Save when the message is a complete save sentence, otherwise null.
        return IsSaveRequest(content) ? Save(context, today) : null;
    }

    /// <summary>Carries out what the model read in a reply to the open save question; the question's proposal version was checked before the model was asked.</summary>
    public static AgentSaveDecision AnswerSave(AgentContext context, AgentPendingSave pending, AgentAnswer answer, DateOnly today)
    {
        // STEP-01: Return NotSaved early when the user declined.
        if (answer.Kind == Decline) return new(NotSaved);
        // STEP-02: Return the result of Save early when the user agreed to save with the known date.
        if (pending.Stage == StageConfirm) return Save(context, today);
        // STEP-03: Save with the date left open when the user does not know it yet.
        if (answer.Kind == Defer) return new(null, Commit: true);
        // STEP-04: Check a named date like any other, or ask again: agreeing to save does not answer which day.
        return answer.Start is { } start ? FromAnswer(context, pending, new(start, answer.End), today) : AskDate(context, today);
    }

    /// <summary>A wish to save the model read in the user's wording: since no question named this version yet, it asks first and never commits on its own.</summary>
    public static AgentSaveDecision RequestSave(AgentContext context, DateOnly today)
    {
        // STEP-01: Prepare proposal, then return the new result early when no ready proposal exists.
        var proposal = context.CurrentProposal;
        if (proposal?.Status != "ready") return new(AgentReplyBuilder.SaveConfirmation(proposal));
        // STEP-02: Return the new result early when HasOpenItem(context.State) holds.
        if (HasOpenItem(context.State)) return new(SaveBlocked(context.State));
        // STEP-03: Return one of two results depending on StartDate() is missing.
        return StartDate(context) is null ? AskDate(context, today)
            : new(AgentReplyBuilder.SaveConfirmation(proposal), new(proposal.ProposalId, proposal.Version, StageConfirm));
    }

    /// <summary>An explicit instruction to save: commits a ready proposal once its start date is known, otherwise says what is missing.</summary>
    private static AgentSaveDecision Save(AgentContext context, DateOnly today)
    {
        // STEP-01: Prepare proposal.
        var proposal = context.CurrentProposal;
        // STEP-02: Return the new result early when HasOpenItem(context.State) holds.
        if (HasOpenItem(context.State)) return new(SaveBlocked(context.State));
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

    // Start dates

    private const string Month = @"(?:/|-|\s*thang\s*)(?<{0}>\d{{1,2}})";
    private const string Year = @"(?:(?:/|-|\s*nam\s*)(?<{0}>\d{{4}}|\d{{2}}))?";
    private static readonly string Joiner = @"\s*(?:-|den|toi)\s*";
    private static readonly Regex Iso = new(@"\b(?<y>\d{4})-(?<m>\d{1,2})-(?<d>\d{1,2})\b", RegexOptions.CultureInvariant);
    private static readonly Regex FullRange = new(@"\b(?<d>\d{1,2})" + string.Format(CultureInfo.InvariantCulture, Month, "m") + string.Format(CultureInfo.InvariantCulture, Year, "y") +
        Joiner + @"(?<d2>\d{1,2})" + string.Format(CultureInfo.InvariantCulture, Month, "m2") + string.Format(CultureInfo.InvariantCulture, Year, "y2") + @"\b", RegexOptions.CultureInvariant);
    private static readonly Regex SameMonthRange = new(@"\b(?<d>\d{1,2})" + Joiner + @"(?<d2>\d{1,2})" + string.Format(CultureInfo.InvariantCulture, Month, "m") +
        string.Format(CultureInfo.InvariantCulture, Year, "y") + @"\b", RegexOptions.CultureInvariant);
    private static readonly Regex Single = new(@"\b(?<d>\d{1,2})" + string.Format(CultureInfo.InvariantCulture, Month, "m") + string.Format(CultureInfo.InvariantCulture, Year, "y") + @"\b", RegexOptions.CultureInvariant);
    private const string Lead = @"^(?:(?:di|bat dau|khoi hanh|chon|tu|vao|ngay)\s+)*";
    // Một con số trơ trọi ("5", "ngày 5") có thể là ngày trong tháng hoặc ngày thứ 5 của lịch: hỏi lại, không đoán.
    private static readonly Regex Ambiguous = new(Lead + @"\d{1,2}(?:\s+(?:nhe|nha|di))*$", RegexOptions.CultureInvariant);

    /// <summary>Today's calendar date for a Vietnam-based traveler; stored instants remain UTC.</summary>
    public static DateOnly Today(TimeProvider clock) => DateOnly.FromDateTime(clock.GetUtcNow().ToOffset(TimeSpan.FromHours(7)).DateTime);

    /// <summary>Returns the date the message states, or null when it states none or an impossible one such as 31/2.</summary>
    public static AgentDateAnswer? ReadDate(string content, DateOnly today)
    {
        // STEP-01: Prepare text through Normalize.
        var text = NormalizeDate(content);
        // STEP-02: Return one of two results depending on Date() exists early when FullRange.Match() has the expected shape and Date() exists.
        if (FullRange.Match(text) is { Success: true } full && Date(full, "d", "m", "y", today) is { } fullStart)
            return Date(full, "d2", "m2", "y2", fullStart) is { } fullEnd ? new(fullStart, fullEnd) : null;
        // STEP-03: Return one of two results depending on Date() exists early when SameMonthRange.Match() has the expected shape and Date() exists.
        if (SameMonthRange.Match(text) is { Success: true } range && Date(range, "d", "m", "y", today) is { } rangeStart)
            return Date(range, "d2", "m", "y", rangeStart) is { } rangeEnd ? new(rangeStart, rangeEnd) : null;
        // STEP-04: Return one of two results depending on Date() exists early when Iso.Match() has the expected shape.
        if (Iso.Match(text) is { Success: true } iso) return Date(iso, "d", "m", "y", today) is { } isoDate ? new(isoDate, null) : null;
        // STEP-05: Return the date when Single.Match() has the expected shape, otherwise null.
        return Single.Match(text) is { Success: true } single && Date(single, "d", "m", "y", today) is { } date ? new(date, null) : null;
    }

    /// <summary>True when the message tries to name a date but <see cref="ReadDate"/> could not use it, so asking again beats treating it as a new request.</summary>
    public static bool LooksLikeDate(string content)
    {
        var text = NormalizeDate(content);
        return Ambiguous.IsMatch(text) || Single.IsMatch(text) || Iso.IsMatch(text) || SameMonthRange.IsMatch(text) || FullRange.IsMatch(text);
    }

    private static DateOnly? Date(Match match, string day, string month, string year, DateOnly notBefore)
    {
        // STEP-01: Prepare d through int.Parse, m through int.Parse, stated from int.Parse and y.
        var d = int.Parse(match.Groups[day].Value, CultureInfo.InvariantCulture);
        var m = int.Parse(match.Groups[month].Value, CultureInfo.InvariantCulture);
        var stated = match.Groups[year] is { Success: true } group ? int.Parse(group.Value, CultureInfo.InvariantCulture) : (int?)null;
        var y = stated is { } value ? value < 100 ? 2000 + value : value : notBefore.Year;
        // STEP-02: Return null early when DateOnly.TryParseExact(CultureInfo.InvariantCulture, DateTimeStyles.None, ...) does not hold.
        if (!DateOnly.TryParseExact($"{y:D4}-{m:D2}-{d:D2}", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return null;
        // STEP-03: Return one of two results depending on stated is missing, date < notBefore and DateOnly.TryParseExact(CultureInfo.InvariantCulture, DateTimeStyles.None, ...) holds.
        // Không nêu năm thì là lần gần nhất sắp tới; năm người dùng nêu thì giữ nguyên để báo "đã qua" thay vì tự đổi.
        return stated is null && date < notBefore && DateOnly.TryParseExact($"{y + 1:D4}-{m:D2}-{d:D2}", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var next) ? next : date;
    }

    private static string NormalizeDate(string text) => Regex.Replace(new string(text.Normalize(NormalizationForm.FormD)
        .Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark).ToArray())
        .Normalize(NormalizationForm.FormC).ToLowerInvariant().Replace('đ', 'd').Trim().TrimEnd('.', '!', '?'), @"\s+", " ");

    // Wording and staged changes

    /// <summary>Recognizes a complete save instruction; negation, quotes, questions and generic yes never commit.</summary>
    public static bool IsSaveRequest(string content) => Regex.IsMatch(Normalize(content),
        @"^(?:(?:toi (?:muon|xac nhan)|hay|dong y) )?(?:luu (?:lich trinh|lich|ban nhap|de xuat)(?: nay| hien tai)?|chot (?:lich trinh|lich|de xuat)(?: nay| hien tai)?|xac nhan luu(?: lich trinh| lich)?(?: nay)?|save (?:this )?(?:itinerary|plan))(?: giup toi| cho toi| nhe| di)?$",
        RegexOptions.CultureInvariant);

    /// <summary>Requests a complete review before saving; this wording never authorizes a roadmap write.</summary>
    public static bool IsReviewRequest(string content) => Regex.IsMatch(Normalize(content),
        @"^(?:hay )?(?:lap|tong hop|hoan thien) (?:lich|lich trinh)(?: giup toi| cho toi)? de chot(?: lich| lich trinh)?$",
        RegexOptions.CultureInvariant);

    /// <summary>Recognizes a request to see the current plan, the only question answered by listing it again.</summary>
    public static bool IsPlanViewRequest(string content) => Regex.IsMatch(Normalize(content),
        @"\b(?:xem|in|liet ke|hien thi|tong hop)\b.*\b(?:lich|lo trinh|roadmap|ke hoach|de xuat|ban nhap)\b|\b(?:lich|lo trinh|roadmap|ke hoach)\b.*\b(?:co gi|the nao|ra sao|hien tai)\b",
        RegexOptions.CultureInvariant);

    /// <summary>Names what still blocks saving, so the user is not left guessing which question is meant.</summary>
    public static string SaveBlocked(AgentState state) => state.PendingQuestion is { } question && state.PendingStateChange is null && state.PendingPlanningRequest is null
        ? $"Trước khi lưu, mình cần bạn trả lời: {question} Nếu không muốn đổi gì, bạn nhắn “giữ nguyên” rồi bảo mình chốt nhé."
        : "Bạn cần trả lời câu hỏi hoặc xác nhận thay đổi điều kiện đang chờ trước khi lưu lịch.";

    /// <summary>Resolves unambiguous draft controls before a provider call; other natural phrases remain model-interpreted.</summary>
    public static string? DraftControl(string content) => Normalize(content) switch
    {
        "bo ban nhap" or "bo nhap" or "huy ban nhap" or "bo de xuat" or "bo ban chinh sua" or "discard draft" => "discard",
        "lam lai tu dau" or "bat dau lai tu dau" or "bat dau ke hoach moi" or "reset" or "start over" => "reset",
        _ => null
    };

    /// <summary>Stages validated proposed constraints, retaining the original state until the user confirms them.</summary>
    public static AgentPreparedTurn StageChange(AgentState current, AgentState proposed, bool hasPlan = true)
    {
        // STEP-01: Prepare changes as List<string>.
        var changes = new List<string>();
        // STEP-02: When current.Province is not proposed.Province, call changes.Add.
        if (current.Province != proposed.Province) changes.Add($"địa điểm: {proposed.Province ?? "chưa chọn"}");
        // STEP-03: When current.NumDays is not proposed.NumDays, call changes.Add.
        if (current.NumDays != proposed.NumDays) changes.Add($"số ngày: {proposed.NumDays?.ToString(CultureInfo.InvariantCulture) ?? "chưa chọn"}");
        // STEP-04: When current.BudgetAmount is not proposed.BudgetAmount or current.BudgetUnrestricted is not proposed.BudgetUnrestricted, call changes.Add.
        if (current.BudgetAmount != proposed.BudgetAmount || current.BudgetUnrestricted != proposed.BudgetUnrestricted)
            changes.Add(proposed.BudgetUnrestricted ? "ngân sách không giới hạn" : $"ngân sách: {proposed.BudgetAmount?.ToString("0", CultureInfo.InvariantCulture) ?? "chưa chọn"} đồng");
        // STEP-05: When current.MaxPricePerMeal is not proposed.MaxPricePerMeal, call changes.Add.
        if (current.MaxPricePerMeal != proposed.MaxPricePerMeal) changes.Add($"trần giá mỗi món: {proposed.MaxPricePerMeal?.ToString("0", CultureInfo.InvariantCulture) ?? "không giới hạn"} đồng");
        // STEP-06: When current.TagIds.SequenceEqual(proposed.TagIds) does not hold or current.ExcludeTagIds.SequenceEqual(proposed.ExcludeTagIds) does not hold, call changes.Add.
        if (!current.TagIds.SequenceEqual(proposed.TagIds) || !current.ExcludeTagIds.SequenceEqual(proposed.ExcludeTagIds)) changes.Add("bộ lọc món ăn");
        // STEP-07: Add the built entries to changes.
        changes.AddRange(MealRequestChanges(current.MealRequests, proposed.MealRequests));
        // STEP-08: When current.RadiusKm is not proposed.RadiusKm, current.OriginMode is not proposed.OriginMode and more.
        if (current.RadiusKm != proposed.RadiusKm || current.OriginMode != proposed.OriginMode || current.OriginLatitude != proposed.OriginLatitude || current.OriginLongitude != proposed.OriginLongitude)
            changes.Add("điểm xuất phát hoặc bán kính tìm món");
        // STEP-09: Prepare reply from string.Join and pending.
        // Chưa có lịch xem trước/đã lưu thì không nhắc "bản lịch chưa thay đổi" để người dùng không tưởng đã có lịch.
        var reply = $"Mình đổi {string.Join("; ", changes)} nhé? Bạn nhắn “ok” để đổi hoặc “thôi” để giữ nguyên." +
            (hasPlan ? " Bản lịch hiện tại chưa thay đổi." : "");
        var pending = proposed with { PendingStateChange = null, PendingQuestion = null, PendingPlanningRequest = null,
            FocusDay = current.FocusDay, FocusMeal = current.FocusMeal,
            SourceRoadmapUpdatedAt = current.SourceRoadmapUpdatedAt, HistoryStartSequence = current.HistoryStartSequence };
        // STEP-10: Return the new result.
        return new(reply, current with { PendingQuestion = reply, PendingStateChange = pending, PendingPlanningRequest = null }, null, null);
    }

    /// <summary>Names each trip/day/meal scope whose dish or restaurant request was added, removed or changed.</summary>
    private static IEnumerable<string> MealRequestChanges(AgentMealRequest[] current, AgentMealRequest[] proposed)
    {
        // STEP-01: Prepare before through current.ToDictionary and after through proposed.ToDictionary.
        var before = current.ToDictionary(x => (x.DayNumber, x.MealSlot));
        var after = proposed.ToDictionary(x => (x.DayNumber, x.MealSlot));
        // STEP-02: For each key in Keys.Union().OrderBy(filtered).ThenBy(filtered), prepare scope from AgentReplyBuilder.Meal and check whether after does not contain key.
        foreach (var key in before.Keys.Union(after.Keys).OrderBy(x => x.DayNumber ?? 0).ThenBy(x => x.MealSlot is null ? -1 : Array.IndexOf(AgentStateValidator.Meals, x.MealSlot)))
        {
            var scope = key.DayNumber is not { } day ? "toàn chuyến" : key.MealSlot is null ? $"ngày {day}" : $"{AgentReplyBuilder.Meal(key.MealSlot)} ngày {day}";
            if (!after.ContainsKey(key)) yield return $"bỏ yêu cầu món/quán cho {scope}";
            else if (!before.ContainsKey(key)) yield return $"thêm yêu cầu món/quán cho {scope}";
            else if (AgentJson.Write(before[key]) != AgentJson.Write(after[key])) yield return $"đổi yêu cầu món/quán cho {scope}";
        }
    }

    /// <summary>Normalizes Vietnamese accents and terminal emphasis without removing negation, quotes or question marks.</summary>
    private static string Normalize(string content)
    {
        var text = new string(content.Normalize(NormalizationForm.FormD).Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark).ToArray())
            .Normalize(NormalizationForm.FormC).ToLowerInvariant().Replace('đ', 'd').Trim().TrimEnd('.', '!').Trim();
        return Regex.Replace(text, @"\s+", " ");
    }
}
