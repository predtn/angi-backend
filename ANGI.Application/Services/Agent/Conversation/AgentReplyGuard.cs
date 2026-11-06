using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Services.Agent.Intake;

namespace ANGI.Application.Services.Agent.Conversation;

/// <summary>
/// Checks what the model wrote before the user sees it. A reply about the plan is shown only when every number and proper name in it is backed by trusted data, and notes are attached after every backend repair so text about a swapped dish never appears.
/// Social replies and dish descriptions change nothing, so they only have to avoid claims the backend alone can make; refusals are backend-owned. A failed check falls back to fixed wording.
/// </summary>
public static class AgentReplyGuard
{
    private const int MaxReplyLength = 500;
    private const int MaxOpenerLength = 120;
    private const int MaxNoteLength = 200;
    private const int MaxSummaryLength = 320;
    // Chữ đầu tên món chỉ tính khi đứng đầu nhiều món: "phở", "bún" là món, còn "lòng", "ba", "bữa" đầu vài món hiếm lại là từ thường ngày ("vui lòng", "ba ngày").
    private const int MinDishesPerWord = 5;
    private static readonly JsonSerializerOptions ReadableJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly HashSet<string> GenericNames = ["angi", "ngay", "bua", "mon", "quan", "lich", "tong", "thu", "chu", "nhat", "viet", "nam"];
    // Cam kết thay đổi chỉ backend được nói sau khi đã kiểm tra; thuật ngữ nội bộ là dấu hiệu model đang lộ dữ liệu kỹ thuật. Đọc trên chữ còn dấu: bỏ dấu thì "đã đói" thành "đã đổi", "vừa thấy" thành "vừa thay".
    private static readonly Regex ChangeClaim = new(@"\b(?:đã|vừa) (?:lưu|đổi|thay|thêm|xóa|xoá|bỏ|cập nhật|chốt|đặt)\b", RegexOptions.CultureInvariant);
    private static readonly Regex InternalTerm = new(@"https?:|www\.|\b(?:catalog|roadmap|proposal|backend|schema|llm|api|prompt|tag|json|grant|itemkey)\b", RegexOptions.CultureInvariant);
    // "Mình sẽ đổi" chỉ là hứa khi nói khẳng định; trong câu hỏi ("mình sẽ đổi món nào nhé?") nó đang xin ý người dùng.
    private static readonly Regex Promise = new(@"\bsẽ (?:lưu|đổi|thay|thêm|xóa|xoá|bỏ|cập nhật|chốt|đặt)\b", RegexOptions.CultureInvariant);
    // Câu tiếng Việt bình thường không có backtick, lời gọi hàm, phép gán hay dấu chấm phẩy cuối dòng; có nghĩa model đang viết code.
    private static readonly Regex Code = new(@"`|\b\w+\([^)]*\)|[=\[\]]|;\s*(?:\n|$)", RegexOptions.CultureInvariant);
    // Dữ liệu món không có các thông tin này, trừ khi câu trả lời đang nói rõ là chưa có.
    private static readonly Regex Unbacked = new(@"\b(?:mo cua|dong cua|noi tieng|ngon nhat|dong khach|halal|di ung|an toan|danh gia cao)\b", RegexOptions.CultureInvariant);
    private static readonly Regex Admits = new(@"\b(?:chua co|khong co|chua ro|khong ro) (?:thong tin|du lieu)\b", RegexOptions.CultureInvariant);
    private static readonly Regex Numbers = new(@"\d+(?:[.,]\d+)*(?<unit>\s*(?:k|nghin|ngan)\b)?", RegexOptions.CultureInvariant);

    /// <summary>Picks the visible reply for a clarify or answer turn: trusted notices first, then checked model wording, then the fixed reply.</summary>
    public static string Choose(AgentContext context, AgentOutput output) => ChooseIssue(context, output) is null
        ? (PlanAsked(context, output) ? output.Reply.Replace(PlanSlot, AgentReplyBuilder.Plan(context), StringComparison.Ordinal) : output.Reply).Trim()
        : output.Action == "clarify" ? AgentReplyBuilder.Clarify(context, output.Clarification) : AgentReplyBuilder.Answer(context, PlanAsked(context, output));

    // Model đọc ý muốn xem lịch theo mọi cách nói và đặt ô này; backend điền lịch thật, nên không cần đoán câu người dùng bằng regex.
    private const string PlanSlot = "{plan}";

    /// <summary>True when the model answered a wish to see the plan with the {plan} slot and a plan exists to fill it.</summary>
    private static bool PlanAsked(AgentContext context, AgentOutput output) =>
        output.Action == "answer" && context.Source is not null && output.Reply.Contains(PlanSlot, StringComparison.Ordinal);

    /// <summary>Why the model's wording cannot be shown on a clarify or answer turn, as a short code for the log; null when it can.</summary>
    public static string? ChooseIssue(AgentContext context, AgentOutput output)
    {
        // STEP-01: When output.Action is "clarify", return the code of the first rule the question breaks.
        if (output.Action == "clarify")
        {
            // Model tự viết câu hỏi để đáp đúng điều người dùng vừa nói. Câu dựa trên bằng chứng của backend (thiếu món, vượt ngân sách, trùng tên) giữ cố định.
            if (output.Clarification is not ("trip_details" or "place_scope" or "preferences" or "edit_scope" or "general" or "taste_intensity" or "taste_scope" or "novelty"))
                return "fixed_question";
            if (context.ToolNotice is not null || context.CatalogNotice is not null) return "backend_notice";
            // Lịch mới đã lấy được gợi ý, đủ thông tin mà model vẫn hỏi chung chung nghĩa là chưa ghép được lịch: nói thẳng là chưa hoàn tất thay vì để câu hỏi che đi.
            if (context.Exchanges.Count > 0 && context.Source is null &&
                (output.Clarification == "general" || output.Clarification is "trip_details" or "edit_scope" && AgentReplyBuilder.TripDetailsQuestion(context.State) is null))
                return "after_tools";
            // Không đòi dấu "?": câu hỏi tiếng Việt hay kết bằng "nhé." và câu cố định thay vào cũng vậy; chỉ dữ kiện trong câu mới cần kiểm.
            return QuestionIssue(output.Reply, context);
        }
        // STEP-02: Return "backend_answer" early when the answer rests on a backend notice.
        if (context.ToolNotice is not null || context.CatalogAnswer is not null) return "backend_answer";
        // STEP-03: Return the result of GroundingIssue on the words around the {plan} slot, which the backend fills itself; the slot alone needs no words.
        if (PlanAsked(context, output) && output.Reply.Replace(PlanSlot, "", StringComparison.Ordinal).Trim().Length == 0) return null;
        return GroundingIssue(PlanAsked(context, output) ? output.Reply.Replace(PlanSlot, " ", StringComparison.Ordinal) : output.Reply, context);
    }

    /// <summary>
    /// What the model must fix for its reply to be shown, as feedback for one rewrite; null when the issue cannot be fixed by rewording,
    /// because the backend owns that reply (a notice, the plan being listed, a question resting on backend evidence).
    /// </summary>
    public static string? RepairHint(string? issue) => issue switch
    {
        null or "fixed_question" or "backend_notice" or "after_tools" or "backend_answer" => null,
        "number" => "it states a number nothing in the data or the user's words backs; leave numbers out",
        "name" => "it names a place, dish or restaurant that neither the user nor the data mentioned",
        "dish_word" => "it names a dish that is not in the data",
        "claim" => "it claims a change, a saved plan or a fact (opening hours, fame, safety) that the backend did not confirm",
        "too_long" => "it is too long; keep it to a few short sentences",
        "empty" => "it is empty",
        "not_vietnamese" => "it is not written in Vietnamese",
        _ => "it is not plain conversational text (markup, code or internal terms)"
    };

    /// <summary>A friendly opening clause for a new preview, or null. It runs before backend repairs, so it may state no fact at all.</summary>
    public static string? Opener(string? reply)
    {
        // STEP-01: Prepare text through reply ?? "".Trim.
        var text = (reply ?? "").Trim();
        // STEP-02: Return null early when text.Length is 0 or > MaxOpenerLength, text.Any matches the filter, IsWellFormed(text) does not hold or 1 more condition(s).
        if (text.Length is 0 or > MaxOpenerLength || text.Any(char.IsDigit) || !IsWellFormed(text) || text[^1] is not ('!' or '.')) return null;
        // STEP-03: Return one of two results depending on Capitalized() has items.
        return Capitalized(text).Any() ? null : text;
    }

    /// <summary>True when the wording is well formed, makes no claim only the backend may make, and all its numbers and names are backed.</summary>
    public static bool IsGrounded(string? reply, AgentContext context) => GroundingIssue(reply, context) is null;

    /// <summary>The first rule the wording breaks, as a short code for the log; null when it may be shown.</summary>
    public static string? GroundingIssue(string? reply, AgentContext context)
    {
        // STEP-01: Prepare text through reply ?? "".Trim.
        var text = (reply ?? "").Trim();
        // STEP-02: Return the result of LengthOrFormIssue early when it exists.
        if (LengthOrFormIssue(text) is { } issue) return issue;
        // STEP-03: Prepare trusted through Trusted, then return the result of FactsIssue.
        var trusted = Trusted(context);
        return FactsIssue(text, trusted, trusted, TrustedNumbers(context, trusted), context.DishWords);
    }

    /// <summary>The check for a question: it states no fact, so it may name a dish or a place freely, but a number in it must still be backed.</summary>
    private static string? QuestionIssue(string reply, AgentContext context)
    {
        // STEP-01: Prepare text through reply.Trim, then return the result of LengthOrFormIssue early when it exists.
        var text = reply.Trim();
        if (LengthOrFormIssue(text) is { } issue) return issue;
        // STEP-02: Prepare trusted through Trusted and return "number" when a number nothing backs is used.
        var trusted = Trusted(context);
        var numbers = TrustedNumbers(context, trusted);
        return Numbers.Matches(text).Any(match => !numbers.Contains(Value(match))) ? "number" : null;
    }

    /// <summary>True when the text claims a change only the backend can make, or uses an internal term.</summary>
    public static bool ClaimsChangeOrInternal(string text) => ChangeClaim.IsMatch(text.Normalize(NormalizationForm.FormC).ToLowerInvariant()) || InternalTerm.IsMatch(Plain(text));

    /// <summary>
    /// The check for the words a model writes around backend-filled slots: every fact belongs in a slot, so only what the user wrote, the trip's own
    /// name or a day of the trip may back a number or a name. A dish's name or price written out instead of its slot fails here, even when it is correct.
    /// </summary>
    public static bool IsGroundedTemplate(string text, AgentContext context) => TemplateIssue(text, context) is null;

    /// <summary>The first rule the words around the slots break, as a short code for the log; null when they may be shown.</summary>
    public static string? TemplateIssue(string text, AgentContext context)
    {
        // STEP-01: Return the result of LengthOrFormIssue early when it exists.
        if (LengthOrFormIssue(text) is { } issue) return issue;
        // STEP-02: Prepare trusted through string.Join, then return the result of FactsIssue.
        var trusted = string.Join(' ', context.Request.Content, context.State.Province, context.State.Title, context.DishSuggestion?.Area, context.DishSuggestion?.AreaNotFound);
        var days =Enumerable.Range(1, context.State.NumDays ?? 0).Select(day => day.ToString(CultureInfo.InvariantCulture));
        // Địa danh có trong dữ liệu (tỉnh của catalog, địa chỉ quán vừa trả về) là dữ kiện thật: câu "ở Hà Nội nè" không bị thay bằng câu cố định. Số trong địa chỉ thì không được tin.
        var names = string.Join(' ', trusted, string.Join(' ', context.CatalogProvinces ?? []),
            string.Join(' ', (context.DishSuggestion?.Dishes ?? []).Select(dish => dish.Address)));
        return FactsIssue(text, trusted, names, Numbers.Matches(trusted).Select(Value).Concat(days).ToHashSet(), context.DishWords);
    }

    private static string? LengthOrFormIssue(string text) => text.Length == 0 ? "empty" : text.Length > MaxReplyLength ? "too_long" : FormIssue(text);

    /// <summary>The first thing in the text the catalog cannot back: a claim, a number, a name or a dish word; null when all of it is backed.</summary>
    private static string? FactsIssue(string text, string trusted, string names, HashSet<string> numbers, IReadOnlySet<string>? dishWords)
    {
        // STEP-01: Return "claim" early when Plain() matches Unbacked and Plain() does not match Admits.
        if (Unbacked.IsMatch(Plain(text)) && !Admits.IsMatch(Plain(text))) return "claim";
        // STEP-02: Return "number" early when numbers does not contain every number in the text.
        if (Numbers.Matches(text).Any(match => !numbers.Contains(Value(match)))) return "number";
        // STEP-03: Return "name" or "dish_word" when the text uses one nothing backs; a dish word must still come from the user or the stated scope, not from the dish just suggested.
        return !NamesBacked(text, names) ? "name" : NamesUnbackedDish(text, trusted, dishWords) ? "dish_word" : null;
    }

    /// <summary>A number as plain digits, with a thousand-unit word ("60k") written out.</summary>
    private static string Value(Match match) => new string(match.Value.Where(char.IsDigit).ToArray()) + (match.Groups["unit"].Success ? "000" : "");

    /// <summary>The words that start at least <see cref="MinDishesPerWord"/> dish names, read from the catalog rather than written down here.</summary>
    public static IReadOnlySet<string> DishWords(IEnumerable<string> dishNames) => dishNames.SelectMany(name => Tokens(name).Take(1))
        .GroupBy(word => word).Where(group => group.Count() >= MinDishesPerWord).Select(group => group.Key).ToHashSet();

    /// <summary>True when the text uses a common dish word nothing trusted backs, the shape of an invented dish written in lower case ("món của bạn là phở"), which the capital-letter check cannot see.</summary>
    internal static bool NamesUnbackedDish(string text, string trusted, IReadOnlySet<string>? dishWords)
    {
        // STEP-01: Return false early when dishWords is missing or empty.
        if (dishWords is null or { Count: 0 }) return false;
        // STEP-02: Prepare known through Tokens(trusted).ToHashSet.
        var known = Tokens(trusted).ToHashSet();
        // STEP-03: Return whether Tokens(text) holds a dish word outside known.
        return Tokens(text).Any(word => dishWords.Contains(word) && !known.Contains(word));
    }

    /// <summary>True when the text looks like code rather than a Vietnamese sentence, the shape a reply takes when the model carries out a request outside ANGI.</summary>
    public static bool IsCode(string text) => Code.IsMatch(text);

    /// <summary>True when every capitalized word inside a sentence is generic or appears in <paramref name="trusted"/>; a sentence's first word cannot be told from a name.</summary>
    internal static bool NamesBacked(string text, string trusted)
    {
        var names = Words(trusted).Split(' ').ToHashSet();
        return Capitalized(text).All(word => GenericNames.Contains(word) || names.Contains(word));
    }

    /// <summary>
    /// The stricter check for text written about a dish or a day: no number at all, because the card already shows the checked ones,
    /// and proper names only from <paramref name="trusted"/>. A note that fails is dropped without the user noticing.
    /// </summary>
    public static bool IsGroundedNote(string? note, string trusted, int maxLength)
    {
        // STEP-01: Prepare text through note ?? "".Trim.
        var text = (note ?? "").Trim();
        // STEP-02: Return false early when text.Length is 0, text.Length > maxLength, text.Any matches the filter or 2 more condition(s).
        if (text.Length == 0 || text.Length > maxLength || text.Any(char.IsDigit) || !IsWellFormed(text) || Unbacked.IsMatch(Plain(text))) return false;
        // STEP-03: Return the result of NamesBacked.
        return NamesBacked(text, trusted);
    }

    /// <summary>Keeps earlier notes for what did not change and adds this turn's checked notes.</summary>
    public static AgentSnapshot AttachNotes(AgentSnapshot snapshot, AgentContext context)
    {
        // STEP-01: Prepare dishNotes through context.Source?.DishNotes ?? [].ToDictionary, written and toolResults through string.Join.
        var dishNotes = (context.Source?.DishNotes ?? []).ToDictionary(note => note.DishId);
        var written = context.PreviewOutput?.DishNotes ?? [];
        var toolResults = string.Join(' ', context.Exchanges.SelectMany(exchange => exchange.Results).Select(ToolText));
        // STEP-02: Go through each note in written.
        foreach (var note in written)
        {
            // STEP-03: Skip this round when snapshot.Items.FirstOrDefault(filtered) is not { } item.
            if (snapshot.Items.FirstOrDefault(item => item.DishId == note.DishId) is not { } item) continue;
            // STEP-04: Prepare trusted through string.Join.
            var trusted = string.Join(' ', item.DishName, item.RestaurantName, snapshot.State.Province, toolResults);
            string? Checked(string? text) => IsGroundedNote(text, trusted, MaxNoteLength) ? text!.Trim() : null;
            // STEP-05: When Checked() is var intro, Checked() is var reason and (intro exists or reason exists), set dishNotes[note.DishId].
            if (Checked(note.Intro) is var intro && Checked(note.Reason) is var reason && (intro is not null || reason is not null))
                dishNotes[note.DishId] = new(note.DishId, intro, reason);
        }
        // STEP-06: Prepare present through snapshot.Items.Select(filtered).ToHashSet.
        var present = snapshot.Items.Select(item => item.DishId).ToHashSet();
        // STEP-07: Return a copy of snapshot with updated fields.
        return snapshot with
        {
            DishNotes = [.. dishNotes.Values.Where(note => present.Contains(note.DishId)).OrderBy(note => note.DishId)],
            DaySummaries = [.. snapshot.Items.Select(item => item.DayNumber).Distinct().Order().SelectMany(day => DaySummary(day, snapshot, context))]
        };
    }

    /// <summary>A day's new summary, or its earlier one if the day still holds the same dishes; an old summary must not describe dishes that left.</summary>
    private static IEnumerable<AgentDaySummary> DaySummary(short day, AgentSnapshot snapshot, AgentContext context)
    {
        // STEP-01: Prepare items through snapshot.Items.Where(filtered).ToArray, trusted through string.Join and written through the computed value.FirstOrDefault.
        var items = snapshot.Items.Where(item => item.DayNumber == day).ToArray();
        var trusted = string.Join(' ', items.SelectMany(item => new[] { item.DishName, item.RestaurantName }).Append(snapshot.State.Province).Append(snapshot.State.Title));
        var written = (context.PreviewOutput?.DaySummaries ?? []).FirstOrDefault(summary => summary.DayNumber == day);
        // STEP-02: When written exists and IsGroundedNote(written.Summary, trusted, ...) holds, apply this case.
        if (written is not null && IsGroundedNote(written.Summary, trusted, MaxSummaryLength))
        {
            yield return new(day, written.Summary.Trim());
            yield break;
        }
        // STEP-03: Prepare before from .Items.Where.
        var before = context.Source?.Items.Where(item => item.DayNumber == day).Select(item => item.DishId).ToHashSet();
        // STEP-04: When before exists, before.SetEquals(...) holds and the computed value exists, apply this case.
        if (before is not null && before.SetEquals(items.Select(item => item.DishId)) &&
            context.Source?.DaySummaries?.FirstOrDefault(summary => summary.DayNumber == day) is { } earlier)
            yield return earlier;
    }

    private static bool IsWellFormed(string text) => FormIssue(text) is null;

    /// <summary>What makes the text unfit to show whatever its facts: wrong language, markup, code, a claimed change or a technical term.</summary>
    private static string? FormIssue(string text)
    {
        // STEP-01: Return "not_vietnamese" early when the text holds no accented letter.
        // Tiếng Việt có dấu là dấu hiệu model trả lời đúng ngôn ngữ; câu tiếng Anh hay câu nháp không bao giờ tới người dùng.
        if (!text.Any(character => character > 127 && char.IsLetter(character))) return "not_vietnamese";
        // STEP-02: Return "markup" early when the text holds a control character, an angle bracket or a brace.
        if (text.Any(character => char.IsControl(character) && character != '\n') || text.IndexOfAny(['<', '>', '{', '}']) >= 0) return "markup";
        // STEP-03: Return "code" early when IsCode(text) holds.
        if (IsCode(text)) return "code";
        // STEP-04: Prepare plain through Plain, then return the code of the first wording only the backend may use.
        var accented = text.Normalize(NormalizationForm.FormC).ToLowerInvariant();
        if (ChangeClaim.IsMatch(accented) || !text.TrimEnd().EndsWith('?') && Promise.IsMatch(accented)) return "claim";
        return InternalTerm.IsMatch(Plain(text)) ? "internal_term" : null;
    }

    /// <summary>All text the reply may quote: the plan, the trip, tool results of this turn and what the user just wrote.</summary>
    private static string Trusted(AgentContext context)
    {
        // STEP-01: Prepare parts as List<string?>.
        var parts = new List<string?> { context.Request.Content, context.State.Province, context.State.Title };
        // STEP-02: Add the built entries to parts.
        parts.AddRange(context.State.MealRequests.SelectMany(request => new[] { request.DishName, request.RestaurantName }));
        // STEP-03: When context.Source exists, call parts.AddRange.
        if (context.Source is { } source) parts.AddRange(source.Items.SelectMany(item => new[] { item.DishName, item.RestaurantName }));
        // STEP-04: Add the built entries to parts, with the examples of the questions before a new plan, which the model may reuse.
        if (!Intake.AgentIntakePolicy.HasPlan(context)) parts.AddRange([Intake.AgentIntakePolicy.PlaceQuestion, Intake.AgentIntakePolicy.PreferencesQuestion, string.Join(' ', context.CatalogProvinces ?? [])]);
        parts.AddRange(context.Exchanges.SelectMany(exchange => exchange.Results).Select(ToolText));
        // STEP-05: Return the result of string.Join.
        return string.Join(' ', parts.Where(part => part is not null));
    }

    /// <summary>A tool result as text with its Vietnamese letters; the raw JSON holds them as \u escapes, which no name in a reply could ever match.</summary>
    private static string ToolText(JsonElement result) => JsonSerializer.Serialize(result, ReadableJson);

    private static HashSet<string> TrustedNumbers(AgentContext context, string trusted)
    {
        // STEP-01: Prepare numbers through Numbers.Matches().Select(filtered).ToHashSet.
        var numbers = Numbers.Matches(trusted).Select(match => new string(match.Value.Where(char.IsDigit).ToArray())).ToHashSet();
        void Add(decimal? value) { if (value is { } amount) numbers.Add(amount.ToString("0", CultureInfo.InvariantCulture)); }
        // STEP-02: Queue context.State.BudgetAmount through Add, queue context.State.MaxPricePerMeal through Add and queue context.State.NumDays through Add.
        Add(context.State.BudgetAmount); Add(context.State.MaxPricePerMeal); Add(context.State.NumDays);
        // STEP-03: When context.Source exists, run the branch below.
        if (context.Source is { } source)
        {
            // STEP-04: For each item in source.Items, call Add.
            foreach (var item in source.Items) Add(item.Price);
            // STEP-05: Queue the built entries through Add.
            Add(source.Items.Sum(item => item.Price));
            // STEP-06: For each day in source.Items.GroupBy(filtered), call Add.
            foreach (var day in source.Items.GroupBy(item => item.DayNumber)) Add(day.Sum(item => item.Price));
            // STEP-07: While count <= Math.Max(), call numbers.Add.
            for (var count = 1; count <= Math.Max(source.Items.Length, source.State.NumDays ?? 0); count++) numbers.Add(count.ToString(CultureInfo.InvariantCulture));
        }
        // STEP-08: While day <= context.State.NumDays ?? 0, call numbers.Add.
        for (var day = 1; day <= (context.State.NumDays ?? 0); day++) numbers.Add(day.ToString(CultureInfo.InvariantCulture));
        // STEP-09: Return numbers.
        return numbers;
    }

    /// <summary>Capitalized words inside a sentence, normalized; these are the likely proper names. A sentence's first word is ordinary capitalization.</summary>
    private static IEnumerable<string> Capitalized(string text)
    {
        foreach (var sentence in Regex.Split(text, @"(?<=[.!?:;\n])\s*"))
        {
            var tokens = Regex.Split(sentence.Trim(), @"[^\p{L}\p{M}\d]+").Where(token => token.Length > 0).ToArray();
            foreach (var token in tokens.Skip(1).Where(token => char.IsUpper(token[0]))) yield return Words(token);
        }
    }

    internal static string Plain(string text) => new string(text.Normalize(NormalizationForm.FormD)
        .Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark).ToArray())
        .Normalize(NormalizationForm.FormC).ToLowerInvariant().Replace('đ', 'd');

    // Giữ dấu: "cá" là món còn "cả" thì không.
    private static IEnumerable<string> Tokens(string text) =>
        Regex.Matches(text.Normalize(NormalizationForm.FormC).ToLowerInvariant(), @"[\p{L}\p{M}]+").Select(match => match.Value);

    private static string Words(string text) => Regex.Replace(Regex.Replace(Plain(text), @"[^a-z0-9 ]+", " "), @"\s+", " ").Trim();

    // Social replies, dish descriptions and refusals

    public const string NotUnderstood = "Mình chưa hiểu rõ ý bạn. Bạn muốn lập lịch ăn uống, tìm món, tìm quán hay đổi món trong lịch?";
    public const string ChatFallback = "Mình luôn sẵn sàng! Bạn muốn tìm món, tìm quán hay lập lịch ăn uống nào?";
    // Câu từ chối do chủ dự án chốt: giữ nguyên văn, không để model viết lại.
    public const string OutOfScope = "Việc này nằm ngoài khả năng của mình rồi. Mình chỉ giúp được về món ăn, quán và lịch ăn uống thôi.";
    // Số biến thể là số lẻ: mỗi lượt chat tăng sequence thêm 2 (tin nhắn và câu trả lời), nên hai lần từ chối liền nhau không trùng câu.
    public static readonly string[] OutOfScopeReplies =
    [
        OutOfScope,
        "Mảng này mình chịu nha, mình chỉ giúp được về món ăn, quán và lịch ăn uống thôi.",
        "Cái này mình không giúp được rồi. Cần tìm món, tìm quán hay lập lịch ăn uống thì cứ nhắn mình nhé."
    ];
    public const string FoodInfoFallback = "Mình chưa có đủ thông tin chắc chắn về món này.";
    private const int MaxChatReplyLength = 500;
    private const int MaxFoodInfoLength = 500;
    private const int MaxDishLength = 40;

    // Giá tiền chỉ backend được nói. Đọc trên chữ còn dấu; số không phải tiền ("15 phút", "2 người") vẫn được dùng trong câu xã giao.
    private static readonly Regex MoneyClaim = new(@"\d[\d.,]*\s*(?:k|nghìn|ngàn|triệu|tr|đồng|đ|vnđ|vnd|usd)\b|\d{4,}|\d{1,3}(?:[.,]\d{3})+|\b(?:vnđ|vnd|usd)\b|\b(?:vài|mấy|chục|trăm|nghìn|ngàn|triệu|tỷ) đồng\b", RegexOptions.CultureInvariant);
    // Hiểu biết chung về món do model viết, không phải dữ liệu ANGI: không được nêu tuyên bố sức khỏe. Đọc trên chữ còn dấu: "ăn toàn món chay" không phải "an toàn".
    private static readonly Regex HealthClaim = new(@"\b(?:an toàn|dị ứng|halal|chữa bệnh|giảm cân|calo)\b", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> GenericFoodWords = ["mon", "mon an", "do an", "an", "quan", "quan an", "thuc an", "am thuc"];

    /// <summary>Shows the model's social reply unless it breaks a rule that holds whatever it is about; a social reply changes nothing, so it is free to talk like a person.</summary>
    public static string ChatReply(string? reply, string content, AgentState state) => ChatIssue(reply, content, state) is null ? reply!.Trim() : ChatFallback;

    /// <summary>The first rule a social reply breaks, as a short code for the log; null when it may be shown.</summary>
    public static string? ChatIssue(string? reply, string content, AgentState state)
    {
        // STEP-01: Prepare text through reply ?? "".Trim, then return the code when its length or form is wrong.
        var text = (reply ?? "").Trim();
        if (SocialFormIssue(text, MaxChatReplyLength) is { } issue) return issue;
        // STEP-02: Return "claim" when text claims a change or a price, or uses an internal term.
        if (ClaimsChangeOrInternal(text) || MoneyClaim.IsMatch(Accented(text))) return "claim";
        // STEP-03: Return "name" when text names something nobody stated, the shape of an out-of-scope answer the model labelled as small talk ("Thủ đô của Pháp là Paris").
        return NamesBacked(text, string.Join(' ', content, state.Province, state.Title)) ? null : "name";
    }

    /// <summary>Why a text cannot be shown whatever it says: empty, too long, markup or code.</summary>
    private static string? SocialFormIssue(string text, int maxLength) => text.Length == 0 ? "empty" : text.Length > maxLength ? "too_long"
        : text.Any(character => char.IsControl(character) && character != '\n') || text.IndexOfAny(['<', '>']) >= 0 ? "markup" : IsCode(text) ? "code" : null;

    /// <summary>
    /// Returns a fixed refusal and repeats the open question, so no model text is shown on this turn and the user keeps their place.
    /// Only this reply repeats it: it is wholly the backend's, so the model has no sentence of its own in which to bring the question back.
    /// A food question naming a dish gets its own wording, because "mình chỉ giúp được về món ăn" would contradict refusing it.
    /// </summary>
    public static string OutOfScopeReply(string content, string? suggestedDish, AgentState state, long? turnId)
    {
        // STEP-01: Prepare the refusal, worded for a named dish when there is one.
        var refusal = NamedDish(content, suggestedDish) is { } dish
            ? $"Mình chưa hướng dẫn được phần này vì ANGI không có dữ liệu về công thức, dinh dưỡng hay sức khỏe. Bạn có muốn mình tìm quán {dish} không?"
            : OutOfScopeReplies[(turnId ?? 0) % OutOfScopeReplies.Length];
        // STEP-02: Return it, followed by the open question when one waits.
        return state.PendingQuestion is { } question ? $"{refusal}\n{question}" : refusal;
    }

    /// <summary>
    /// Shows the model's description of a dish when it states no number, health claim, change, code or internal term; otherwise a fixed line. Both offer to find the dish.
    /// Proper names stay allowed, because where a dish comes from is part of describing it.
    /// </summary>
    public static string FoodInfoReply(string? reply, string content, string? suggestedDish)
    {
        // STEP-01: Prepare offer from NamedDish.
        var offer = NamedDish(content, suggestedDish) is { } dish ? $" Bạn có muốn mình tìm quán {dish} trên ANGI không?" : "";
        // STEP-02: Return the checked description followed by the offer.
        return (FoodInfoIssue(reply) is null ? reply!.Trim() : FoodInfoFallback) + offer;
    }

    /// <summary>The first rule a dish description breaks, as a short code for the log; null when it may be shown.</summary>
    public static string? FoodInfoIssue(string? reply)
    {
        // STEP-01: Prepare text through reply ?? "".Trim.
        var text = (reply ?? "").Trim();
        // STEP-02: Return the code when its form is wrong, it states a number, or it claims a change or a health benefit.
        if (SocialFormIssue(text, MaxFoodInfoLength) is { } issue) return issue;
        if (text.Any(char.IsDigit)) return "number";
        return ClaimsChangeOrInternal(text) || HealthClaim.IsMatch(Accented(text)) ? "claim" : null;
    }

    /// <summary>The dish the model picked out, only when the user's own message names it and it is more than a generic food word.</summary>
    private static string? NamedDish(string content, string? suggestedDish)
    {
        var dish = (suggestedDish ?? "").Trim();
        return dish.Length is >= 2 and <= MaxDishLength && Regex.IsMatch(dish, @"^[\p{L}\p{M} ]+$", RegexOptions.CultureInvariant) &&
            !GenericFoodWords.Contains(Words(dish)) && $" {Words(content)} ".Contains($" {Words(dish)} ", StringComparison.Ordinal) ? dish : null;
    }

    private static string Accented(string text) => text.Normalize(NormalizationForm.FormC).ToLowerInvariant();
}
