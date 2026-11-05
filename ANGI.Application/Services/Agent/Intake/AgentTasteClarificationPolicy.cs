using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Services.Agent.Constraints;
using ANGI.Application.Services.Agent.Conversation;

namespace ANGI.Application.Services.Agent.Intake;

/// <summary>
/// Stops direct, underspecified taste preferences before the model can invent a target or scope.
/// Also asks what "món lạ / món mới / món chưa ăn" means before the model can turn it into a dish name or an invented filter.
/// No catalog tag means "new"; the only real filter is differentFromPlan, and ANGI has no eating history.
/// </summary>
public static class AgentTasteClarificationPolicy
{
    private const string Taste = "cay|chua|ngot|man|dang|beo|umami|thanh mat|chat|tuoi|dam da|thom";
    // "thích/muốn ăn cay" và dạng giới hạn "đừng/không cay quá", "bớt cay": cả hai đều chưa nêu rõ mức.
    private const string Preference = @"\b(?:thich|muon|uu tien)(?:\s+(?:an|vi|mon|do|khau vi))*\s+(?:(?:rat|hoi|it|khong)\s+)*(?<taste>" + Taste + @")\b[^,;.!?\n]*" +
        @"|\b(?:dung|khong)\s+(?:an\s+)?(?<taste>" + Taste + @")\s+(?:qua|lam)\b[^,;.!?\n]*|\bbot\s+(?<taste>" + Taste + @")\b[^,;.!?\n]*";
    // Nhãn hiển thị cũng là cách viết có dấu đúng của từ chỉ vị, dùng để phân biệt "chua" với "chưa", "mặn" với "mận".
    private static readonly Dictionary<string, string> Labels = new()
    {
        ["cay"] = "cay", ["chua"] = "chua", ["ngot"] = "ngọt", ["man"] = "mặn",
        ["dang"] = "đắng", ["beo"] = "béo", ["umami"] = "umami", ["thanh mat"] = "thanh mát",
        ["chat"] = "chát", ["tuoi"] = "tươi", ["dam da"] = "đậm đà", ["thom"] = "thơm"
    };

    // Câu có dấu: so đúng chữ, nên "chưa" không phải "chua", "lạ" không phải "là"; "mới" chỉ tính khi đi liền "món".
    private const string AccentedWording = @"\bchưa\s+(?:từng\s+|bao giờ\s+)?(?:ăn|thử)\b|\b(?:ăn|thử)\s+bao giờ\s+chưa\b|" +
        @"\bmón(?:\s+\p{L}+){0,2}\s+lạ\b|\blạ\s+miệng\b|\bđộc\s+lạ\b|\bmón\s+mới\b";
    // Câu không dấu chỉ bắt cụm ít nhập nhằng: "mon la" có thể là "món là" nên không bắt.
    private const string UnaccentedWording = @"\bchua\s+(?:tung\s+|bao gio\s+)?(?:an|thu)\b|\bla mieng\b|\bdoc la\b|\bmon moi\b";
    // Đã nói rõ là khác lịch hiện tại: model dùng differentFromPlan, không cần hỏi.
    private const string DifferentFromPlan = @"\bkhac\s+(?:(?:voi|cac|nhung|het)\s+)*(?:mon\s+)?(?:(?:trong|o|cua)\s+)?(?:lich|lich trinh|ke hoach|de xuat)\b|\bkhong\s+trung\b";
    private const string NeverEaten = @"\bchua\s+(?:tung\s+|bao gio\s+)?(?:an|thu)\b";

    /// <summary>Uses explicit user wording only; persisted model-chosen targets are not proof of human confirmation.</summary>
    public static string? Question(AgentContext context)
    {
        // STEP-01: Prepare current through Normalize, declarations through Declarations, evidence, followingAnswer and 1 more.
        var current = Normalize(context.Request.Content);
        var declarations = Declarations(context.Request.Content);
        var evidence = current;
        var followingAnswer = false;
        var answerIntensity = false;
        // STEP-02: When IsQuestion(State.PendingQuestion) holds or context.ReplanAfterConfirmation is true, prepare start through context.History.FindLastIndex and check whether start >= 0.
        if (IsQuestion(context.State.PendingQuestion) || context.ReplanAfterConfirmation)
        {
            // Keep partial answers across turns: e.g. "vừa" followed by "toàn chuyến".
            var start = context.History.FindLastIndex(turn => turn.Role == "user" && Declarations(turn.Content).Length > 0);
            if (start >= 0)
            {
                var priorDeclarations = Declarations(context.History[start].Content);
                if (IsIgnored(context)) return null;
                if (declarations.Length == 0 || declarations.All(match => priorDeclarations.Any(prior => prior.Groups["taste"].Value == match.Groups["taste"].Value)))
                {
                    if (declarations.Length == 0) declarations = priorDeclarations;
                    followingAnswer = true;
                    evidence = string.Join(' ', context.History.Skip(start).Where(turn => turn.Role == "user")
                        .Select(turn => Normalize(turn.Content)).Append(current));
                    answerIntensity = context.History.Skip(start + 1).Where(turn => turn.Role == "user")
                        .Select(turn => Normalize(turn.Content)).Append(current).Any(IsIntensityAnswer);
                }
            }
        }
        // STEP-03: Return null early when declarations.Length is 0.
        if (declarations.Length == 0) return null;

        // STEP-04: Prepare unresolvedIntensity as List<string> and unresolvedScope as List<string>.
        var unresolvedIntensity = new List<string>();
        var unresolvedScope = new List<string>();
        // STEP-05: Go through each declaration in declarations.DistinctBy(filtered).
        foreach (var declaration in declarations.DistinctBy(match => match.Groups["taste"].Value))
        {
            // STEP-06: Prepare taste, label and historical from context.History.Where.
            var taste = declaration.Groups["taste"].Value;
            var label = Labels[taste];
            var historical = context.State.TasteTargets.Any(target => target.Dimension == Dimension(taste)) &&
                context.History.Where(turn => turn.Role == "user")
                    .Any(turn => Declarations(turn.Content).Any(match => match.Groups["taste"].Value == taste && HasIntensity(match.Value)) && HasScope(Normalize(turn.Content)));
            // STEP-07: When HasIntensity(declaration.Value) does not hold, the computed value is false and historical is false, call unresolvedIntensity.Add.
            if (!HasIntensity(declaration.Value) && !(followingAnswer && (HasIntensity(evidence) || answerIntensity)) && !historical)
                unresolvedIntensity.Add(label);
            // STEP-08: When HasScope(current) does not hold, followingAnswer && HasScope(evidence) is false and historical is false, call unresolvedScope.Add.
            if (!HasScope(current) && !(followingAnswer && HasScope(evidence)) && !historical)
                unresolvedScope.Add(label);
        }
        // STEP-09: Prepare questions as List<string>.
        var questions = new List<string>();
        // STEP-10: When unresolvedIntensity.Count > 0, call questions.Add.
        if (unresolvedIntensity.Count > 0)
            questions.Add($"Bạn muốn mức {string.Join(", ", unresolvedIntensity)} như thế nào: không, nhẹ, vừa, nhiều hay rất nhiều?");
        // STEP-11: When unresolvedScope.Count > 0, call questions.Add.
        if (unresolvedScope.Count > 0)
            questions.Add($"Bạn muốn áp dụng khẩu vị {string.Join(", ", unresolvedScope)} cho toàn chuyến hay ngày/bữa cụ thể nào?");
        // STEP-12: Return one of two results depending on questions.Count is 0.
        return questions.Count == 0 ? null : string.Join(' ', questions);
    }

    /// <summary>
    /// The taste, level and scope the user just gave in answer to the taste question ("Ít, toàn chuyến đi"), as feedback for the model;
    /// null unless one taste has an explicit level and scope. Used when the model asks again although the answer is already complete.
    /// </summary>
    public static string? Resolution(AgentContext context)
    {
        // STEP-01: Return null early when IsQuestion(State.PendingQuestion) does not hold, context.ReplanAfterConfirmation is true, IsIgnored(context) holds or 1 more condition(s).
        if (!IsQuestion(context.State.PendingQuestion) || context.ReplanAfterConfirmation || IsIgnored(context) || Question(context) is not null) return null;
        // STEP-02: Prepare start through context.History.FindLastIndex.
        var start = context.History.FindLastIndex(turn => turn.Role == "user" && Declarations(turn.Content).Length > 0);
        // STEP-03: Return null early when start < 0.
        if (start < 0) return null;
        // STEP-04: Prepare tastes through Select(filtered).Distinct().ToArray.
        var tastes = Declarations(context.History[start].Content).Select(match => match.Groups["taste"].Value).Distinct().ToArray();
        // STEP-05: Return null early when tastes.Length is not 1.
        if (tastes.Length != 1) return null;
        // STEP-06: Prepare messages through Select(filtered).Append().ToArray and level through messages.Select(filtered).LastOrDefault.
        var messages = context.History.Skip(start).Where(turn => turn.Role == "user").Select(turn => turn.Content).Append(context.Request.Content).ToArray();
        var level = messages.Select(message => Level(Normalize(message), tastes[0])).LastOrDefault(value => value.HasValue);
        // STEP-07: Return null early when level is not { } target or AnswerScope() is not { } scope.
        if (level is not { } target || AnswerScope(messages, context.State) is not { } scope) return null;
        // STEP-08: Prepare name.
        var name = target switch { 0d => "không", .25 => "nhẹ", .5 => "vừa", .75 => "nhiều", _ => "rất nhiều" };
        // STEP-09: Return the computed condition.
        return $"Người dùng đã trả lời câu hỏi khẩu vị: {Labels[tastes[0]]} mức {name} ({Dimension(tastes[0])}={target.ToString(CultureInfo.InvariantCulture)}) " +
            $"cho {scope.Text}. Ghi đúng giá trị này vào {scope.Field} rồi lập lịch ngay; không hỏi lại mức hay phạm vi.";
    }

    // Cùng thang với câu hỏi: không 0, nhẹ/ít/hơi 0.25, vừa 0.5, nhiều 0.75, rất nhiều 1; "không cay quá" chưa phải một mức.
    private static double? Level(string text, string taste)
    {
        var word = Regex.Match(text, @"\b(?<w>khong|it|hoi|rat)\s+(?:" + taste + @")\b(?!\s+(?:qua|lam)\b)", RegexOptions.CultureInvariant) is { Success: true } before
            ? before.Groups["w"].Value
            : Regex.Match(text, @"\b(?:" + taste + @")\s+(?<w>rat nhieu|nhe|vua|nhieu)\b", RegexOptions.CultureInvariant) is { Success: true } after
                ? after.Groups["w"].Value
                : IsIntensityAnswer(text) ? Regex.Match(text, @"\b(?<w>rat nhieu|khong|it|hoi|nhe|vua|nhieu|thap|cao|manh)\b", RegexOptions.CultureInvariant).Groups["w"].Value : "";
        return word switch
        {
            "khong" => 0, "it" or "hoi" or "nhe" or "thap" => .25, "vua" => .5, "nhieu" or "cao" or "manh" => .75, "rat" or "rat nhieu" => 1, _ => null
        };
    }

    // Phạm vi rõ ràng duy nhất: bữa của ngày, ngày, hoặc toàn chuyến. Bữa không kèm ngày cần số ngày nên để model xử lý.
    private static (string Text, string Field)? AnswerScope(IEnumerable<string> messages, AgentState state)
    {
        // STEP-01: Prepare references through messages.Select(filtered).ToArray and meals through SelectMany(filtered).Distinct().ToArray.
        var references = messages.Select(message => AgentMealScopePolicy.Read(message, state)).ToArray();
        var meals = references.SelectMany(x => x.Meals).Distinct().ToArray();
        // STEP-02: Return the collected values early when meals.Length > 0.
        if (meals.Length > 0)
            return (string.Join(", ", meals.Select(x => $"{AgentReplyBuilder.Meal(x.MealSlot!)} ngày {x.DayNumber}")), "tasteOverrides với dayNumber và mealSlot đó");
        // STEP-03: Prepare days through Distinct().Order().ToArray.
        var days = references.SelectMany(x => x.Days).Distinct().Order().ToArray();
        // STEP-04: Return the collected values early when days.Length > 0.
        if (days.Length > 0) return (string.Join(", ", days.Select(day => $"ngày {day}")), "tasteOverrides với dayNumber đó và mealSlot=null");
        // STEP-05: Return one of two results depending on messages.Any matches the filter.
        return messages.Any(message => Regex.IsMatch(Normalize(message), @"\b(?:(?:ca|toan)\s+(?:bo\s+)?(?:chuyen|lich|\d+\s+ngay)|tat ca(?: cac)?\s+(?:ngay|bua)|moi bua)\b",
            RegexOptions.CultureInvariant)) ? ("toàn chuyến", "tasteTargets") : null;
    }

    /// <summary>True when the message itself declares a taste preference ("thích ăn chua"), so its day/meal wording may scope that taste.</summary>
    public static bool StatesTaste(string content) => Declarations(content).Length > 0;

    /// <summary>Display names of the tastes the message names ("cay", "ngọt"), ignoring look-alike words such as "chưa" or "mận".</summary>
    public static string[] MentionedTastes(string content)
    {
        var (text, accented) = (Normalize(content), Accented(content));
        return Regex.Matches(text, @"\b(?:" + Taste + @")\b", RegexOptions.CultureInvariant).Cast<Match>()
            .Where(match => SpelledAsTaste(text, accented, match.Value, match.Index)).Select(match => Labels[match.Value]).Distinct().ToArray();
    }

    /// <summary>
    /// True when an open taste question gets a reply with no intensity, scope, taste or duration/budget ("tôi đâu có nói chua"):
    /// the question is dropped instead of repeated, so the user is never stuck. A real answer keeps the question as the model's context.
    /// Confirming a constraint change still asks the taste that remains unresolved.
    /// </summary>
    public static bool IsIgnored(AgentContext context)
    {
        // STEP-01: Return false early when IsQuestion(State.PendingQuestion) does not hold, context.ReplanAfterConfirmation is true, Declarations().Length > 0 or 1 more condition(s).
        if (!IsQuestion(context.State.PendingQuestion) || context.ReplanAfterConfirmation || Declarations(context.Request.Content).Length > 0 ||
            !context.History.Any(turn => turn.Role == "user" && Declarations(turn.Content).Length > 0)) return false;
        // STEP-02: Prepare current through Normalize.
        var current = Normalize(context.Request.Content);
        // STEP-03: Return the computed condition.
        return !IsIntensityAnswer(current) && !HasIntensity(current) && !HasScope(current) && !StatesTripFacts(context.Request.Content);
    }

    /// <summary>True for the fixed taste questions this policy asks, so the next message can be read as their answer.</summary>
    public static bool IsQuestion(string? question) => question is not null &&
        (question.StartsWith("Bạn muốn mức ", StringComparison.Ordinal) || question.StartsWith("Bạn muốn áp dụng khẩu vị ", StringComparison.Ordinal));

    /// <summary>Returns the fixed novelty question, the no-history answer to that question, or null when the model should read the message.</summary>
    public static string? NoveltyQuestion(AgentContext context)
    {
        // STEP-01: Prepare text through Normalize.
        var text = Normalize(context.Request.Content);
        // STEP-02: Return one of two results depending on text matches Regex and text does not match Regex early when the computed value is true.
        // Trả lời câu hỏi novelty: chọn "chưa từng ăn" thì nói thẳng là không có dữ liệu thay vì để model lọc bừa;
        // chọn "khác lịch hiện tại" hoặc câu khác thì model xử lý như cũ.
        if (context.State.PendingQuestion?.StartsWith(AgentReplyBuilder.NoveltyQuestion, StringComparison.Ordinal) == true)
            return Regex.IsMatch(text, NeverEaten, RegexOptions.CultureInvariant) && !Regex.IsMatch(text, DifferentFromPlan, RegexOptions.CultureInvariant)
                ? context.Source is null && context.Roadmap is null ? AgentReplyBuilder.NoveltyWithoutHistoryOrPlan : AgentReplyBuilder.NoveltyWithoutHistory
                : null;
        // STEP-03: Prepare accented through Accented and wording from Regex.Match.
        var accented = Accented(context.Request.Content);
        var wording = accented != text
            ? Regex.Match(accented, AccentedWording, RegexOptions.CultureInvariant)
            : Regex.Match(text, UnaccentedWording, RegexOptions.CultureInvariant);
        // STEP-04: Return null early when wording.Success is false, text matches Regex or Negated(text, accented, ...) holds.
        if (!wording.Success || Regex.IsMatch(text, DifferentFromPlan, RegexOptions.CultureInvariant) || Negated(text, accented, wording.Index)) return null;
        // STEP-05: Return AgentReplyBuilder.NoveltyQuestion.
        return AgentReplyBuilder.NoveltyQuestion;
    }

    // "Không cần món lạ", "đừng chọn món mới": người dùng đang từ chối món lạ, không cần hỏi nghĩa của nó.
    private static bool Negated(string text, string accented, int index)
    {
        // STEP-01: Return false early when accented.Length is not text.Length.
        if (accented.Length != text.Length) return false;
        // STEP-02: Prepare prefix and clause from prefix.LastIndexOfAny.
        var prefix = text[..index];
        var clause = prefix[(prefix.LastIndexOfAny([',', ';', '.', '!', '?', '\n']) + 1)..];
        // STEP-03: Return the result of Regex.IsMatch.
        return Regex.IsMatch(clause, @"\b(?:khong|dung|chang|khoi)\b", RegexOptions.CultureInvariant);
    }

    private static Match[] Declarations(string content)
    {
        var (text, accented) = (Normalize(content), Accented(content));
        return Regex.Matches(text, Preference, RegexOptions.CultureInvariant).Cast<Match>()
            .Where(match => !Regex.IsMatch(text[..match.Index], @"\b(?:khong|chang|chua|dung|khong he)\s*$", RegexOptions.CultureInvariant) &&
                SpelledAsTaste(text, accented, match.Groups["taste"].Value, match.Groups["taste"].Index)).ToArray();
    }

    // So khớp chạy trên bản bỏ dấu để người gõ không dấu vẫn được hiểu. Khi câu có dấu, từ phải đúng cách viết của vị:
    // "chưa" (not yet) không phải "chua", "mận" không phải "mặn", "đang" không phải "đắng". Câu không dấu giữ cách đọc cũ.
    private static bool SpelledAsTaste(string text, string accented, string taste, int index)
    {
        // STEP-01: Return true early when accented.Length is not text.Length.
        if (accented.Length != text.Length) return true;
        // STEP-02: Prepare word through accented.Substring.
        var word = accented.Substring(index, taste.Length);
        // STEP-03: Return the computed condition.
        return word == Labels[taste] || word == taste && accented == text;
    }

    /// <summary>True when the message states a duration or budget, i.e. it answers the trip part of a combined question.</summary>
    private static bool StatesTripFacts(string content)
    {
        var facts = AgentPendingPlanningPolicy.Capture(null, content);
        return facts.NumDays.HasValue || facts.TotalBudget.HasValue || facts.DailyBudget.HasValue;
    }

    // "không cay" là mức không, nhưng "không cay quá" chỉ là giới hạn chưa rõ mức nên không tính.
    private static bool HasIntensity(string text) => Regex.IsMatch(text,
        @"\b(?:khong|it|hoi|rat)\s+(?:" + Taste + @")\b(?!\s+(?:qua|lam)\b)|\b(?:" + Taste + @")\s+(?:nhe|vua|nhieu|rat nhieu|cao|thap|manh)\b|\bmuc(?: do)?\s+(?:khong|nhe|vua|nhieu|rat nhieu|cao|thap|0(?:[.,](?:25|5|75))?|1)\b|\b(?:" + Taste + @")\s+(?:0(?:[.,](?:25|5|75))?|1)\b",
        RegexOptions.CultureInvariant);

    private static bool IsIntensityAnswer(string text) => Regex.IsMatch(text,
        @"^(?:(?:toi|minh|em)\s+)?(?:(?:chon|muon)\s+)?(?:muc(?: do)?\s+)?(?:khong|it|hoi|nhe|vua(?: phai)?|nhieu|rat nhieu|cao|thap|manh|0(?:[.,](?:25|5|75))?|1)\b(?:[\s,;.!]*$|[\s,;]+(?:cho|ap dung|trong|ca|toan|ngay|bua|thoi|nhe)\b.*)",
        RegexOptions.CultureInvariant);

    private static bool HasScope(string text) => Regex.IsMatch(text,
        @"\b(?:(?:ca|toan(?: bo)?)\s+(?:chuyen(?: di)?|lich(?: trinh)?|\d+\s+ngay)|tat ca(?: cac)?\s+(?:ngay|bua)|moi bua|ngay\s+\d+|bua\s+(?:sang|trua|toi|phu|khuya|(?:thu\s+)?(?:\d{1,2}|nhat|hai|ba|bon|nam)|dau|cuoi))\b",
        RegexOptions.CultureInvariant);


    private static string Dimension(string taste) => taste switch
    {
        "cay" => "spicy", "chua" => "sour", "ngot" => "sweet", "man" => "salty", "dang" => "bitter",
        "beo" => "fatty", "thanh mat" => "cooling", "chat" => "astringent", "tuoi" => "fresh",
        "dam da" => "rich", "thom" => "aromatic", _ => taste
    };

    private static string Normalize(string text)
    {
        return new string(Unquote(text).Normalize(NormalizationForm.FormD)
            .Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark).ToArray())
            .Normalize(NormalizationForm.FormC).ToLowerInvariant().Replace('đ', 'd').Trim();
    }

    // Cùng độ dài với Normalize cho chữ tiếng Việt dựng sẵn (mỗi chữ có dấu ứng với đúng một chữ không dấu), nên dùng chung vị trí.
    private static string Accented(string text) => Unquote(text).Normalize(NormalizationForm.FormC).ToLowerInvariant().Trim();

    private static string Unquote(string text) => Regex.Replace(text, "\"[^\"]*\"|'[^']*'|“[^”]*”|‘[^’]*’|`[^`]*`", " ");
}
