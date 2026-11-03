using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ANGI.Application.Common.Models.Agent;

namespace ANGI.Application.Services.Agent;

/// <summary>Stops direct, underspecified taste preferences before the model can invent a target or scope.</summary>
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

    /// <summary>Uses explicit user wording only; persisted model-chosen targets are not proof of human confirmation.</summary>
    public static string? Question(AgentContext context)
    {
        var current = Normalize(context.Request.Content);
        var declarations = Declarations(context.Request.Content);
        var evidence = current;
        var followingAnswer = false;
        var answerIntensity = false;
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
        if (declarations.Length == 0) return null;

        var unresolvedIntensity = new List<string>();
        var unresolvedScope = new List<string>();
        foreach (var declaration in declarations.DistinctBy(match => match.Groups["taste"].Value))
        {
            var taste = declaration.Groups["taste"].Value;
            var label = Labels[taste];
            var historical = context.State.TasteTargets.Any(target => target.Dimension == Dimension(taste)) &&
                context.History.Where(turn => turn.Role == "user")
                    .Any(turn => Declarations(turn.Content).Any(match => match.Groups["taste"].Value == taste && HasIntensity(match.Value)) && HasScope(Normalize(turn.Content)));
            if (!HasIntensity(declaration.Value) && !(followingAnswer && (HasIntensity(evidence) || answerIntensity)) && !historical)
                unresolvedIntensity.Add(label);
            if (!HasScope(current) && !(followingAnswer && HasScope(evidence)) && !historical)
                unresolvedScope.Add(label);
        }
        var questions = new List<string>();
        if (unresolvedIntensity.Count > 0)
            questions.Add($"Bạn muốn mức {string.Join(", ", unresolvedIntensity)} như thế nào: không, nhẹ, vừa, nhiều hay rất nhiều?");
        if (unresolvedScope.Count > 0)
            questions.Add($"Bạn muốn áp dụng khẩu vị {string.Join(", ", unresolvedScope)} cho toàn chuyến hay ngày/bữa cụ thể nào?");
        return questions.Count == 0 ? null : string.Join(' ', questions);
    }

    /// <summary>
    /// The taste, level and scope the user just gave in answer to the taste question ("Ít, toàn chuyến đi"), as feedback for the model;
    /// null unless one taste has an explicit level and scope. Used when the model asks again although the answer is already complete.
    /// </summary>
    public static string? Resolution(AgentContext context)
    {
        if (!IsQuestion(context.State.PendingQuestion) || context.ReplanAfterConfirmation || IsIgnored(context) || Question(context) is not null) return null;
        var start = context.History.FindLastIndex(turn => turn.Role == "user" && Declarations(turn.Content).Length > 0);
        if (start < 0) return null;
        var tastes = Declarations(context.History[start].Content).Select(match => match.Groups["taste"].Value).Distinct().ToArray();
        if (tastes.Length != 1) return null;
        var messages = context.History.Skip(start).Where(turn => turn.Role == "user").Select(turn => turn.Content).Append(context.Request.Content).ToArray();
        var level = messages.Select(message => Level(Normalize(message), tastes[0])).LastOrDefault(value => value.HasValue);
        if (level is not { } target || AnswerScope(messages, context.State) is not { } scope) return null;
        var name = target switch { 0d => "không", .25 => "nhẹ", .5 => "vừa", .75 => "nhiều", _ => "rất nhiều" };
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
        var references = messages.Select(message => AgentMealScopePolicy.Read(message, state)).ToArray();
        var meals = references.SelectMany(x => x.Meals).Distinct().ToArray();
        if (meals.Length > 0)
            return (string.Join(", ", meals.Select(x => $"{AgentReplyBuilder.Meal(x.MealSlot!)} ngày {x.DayNumber}")), "tasteOverrides với dayNumber và mealSlot đó");
        var days = references.SelectMany(x => x.Days).Distinct().Order().ToArray();
        if (days.Length > 0) return (string.Join(", ", days.Select(day => $"ngày {day}")), "tasteOverrides với dayNumber đó và mealSlot=null");
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
        if (!IsQuestion(context.State.PendingQuestion) || context.ReplanAfterConfirmation || Declarations(context.Request.Content).Length > 0 ||
            !context.History.Any(turn => turn.Role == "user" && Declarations(turn.Content).Length > 0)) return false;
        var current = Normalize(context.Request.Content);
        return !IsIntensityAnswer(current) && !HasIntensity(current) && !HasScope(current) && !StatesTripFacts(context.Request.Content);
    }

    /// <summary>True for the fixed taste questions this policy asks, so the next message can be read as their answer.</summary>
    public static bool IsQuestion(string? question) => question is not null &&
        (question.StartsWith("Bạn muốn mức ", StringComparison.Ordinal) || question.StartsWith("Bạn muốn áp dụng khẩu vị ", StringComparison.Ordinal));

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
        if (accented.Length != text.Length) return true;
        var word = accented.Substring(index, taste.Length);
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
