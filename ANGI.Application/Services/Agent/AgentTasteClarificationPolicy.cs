using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ANGI.Application.Common.Models.Agent;

namespace ANGI.Application.Services.Agent;

/// <summary>Stops direct, underspecified taste preferences before the model can invent a target or scope.</summary>
public static class AgentTasteClarificationPolicy
{
    private const string Taste = "cay|chua|ngot|man|dang|beo|umami|thanh mat|chat|tuoi|dam da|thom";
    private const string Preference = @"\b(?:thich|muon|uu tien)(?:\s+(?:an|vi|mon|do|khau vi))*\s+(?:(?:rat|hoi|it|khong)\s+)*(?<taste>" + Taste + @")\b[^,;.!?\n]*";
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
        var declarations = Declarations(current);
        var evidence = current;
        var followingAnswer = false;
        var answerIntensity = false;
        if (IsOurQuestion(context.State.PendingQuestion) || context.ReplanAfterConfirmation)
        {
            // Keep partial answers across turns: e.g. "vừa" followed by "toàn chuyến".
            var start = context.History.FindLastIndex(turn => turn.Role == "user" && Declarations(Normalize(turn.Content)).Length > 0);
            if (start >= 0)
            {
                var previous = Normalize(context.History[start].Content);
                var priorDeclarations = Declarations(previous);
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
                context.History.Where(turn => turn.Role == "user").Select(turn => Normalize(turn.Content))
                    .Any(text => Declarations(text).Any(match => match.Groups["taste"].Value == taste && HasIntensity(match.Value)) && HasScope(text));
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

    private static Match[] Declarations(string text) => Regex.Matches(text, Preference, RegexOptions.CultureInvariant)
        .Cast<Match>().Where(match => !Regex.IsMatch(text[..match.Index], @"\b(?:khong|chang|chua|dung|khong he)\s*$", RegexOptions.CultureInvariant)).ToArray();

    private static bool HasIntensity(string text) => Regex.IsMatch(text,
        @"\b(?:khong|it|hoi|rat)\s+(?:" + Taste + @")\b|\b(?:" + Taste + @")\s+(?:nhe|vua|nhieu|rat nhieu|cao|thap|manh)\b|\bmuc(?: do)?\s+(?:khong|nhe|vua|nhieu|rat nhieu|cao|thap|0(?:[.,](?:25|5|75))?|1)\b|\b(?:" + Taste + @")\s+(?:0(?:[.,](?:25|5|75))?|1)\b",
        RegexOptions.CultureInvariant);

    private static bool IsIntensityAnswer(string text) => Regex.IsMatch(text,
        @"^(?:(?:toi|minh|em)\s+)?(?:(?:chon|muon)\s+)?(?:muc(?: do)?\s+)?(?:khong|it|hoi|nhe|vua(?: phai)?|nhieu|rat nhieu|cao|thap|manh|0(?:[.,](?:25|5|75))?|1)\b(?:[\s,;.!]*$|[\s,;]+(?:cho|ap dung|trong|ca|toan|ngay|bua|thoi|nhe)\b.*)",
        RegexOptions.CultureInvariant);

    private static bool HasScope(string text) => Regex.IsMatch(text,
        @"\b(?:(?:ca|toan(?: bo)?)\s+(?:chuyen(?: di)?|lich(?: trinh)?|\d+\s+ngay)|tat ca(?: cac)?\s+(?:ngay|bua)|moi bua|ngay\s+\d+|bua\s+(?:sang|trua|toi|phu|khuya))\b",
        RegexOptions.CultureInvariant);

    private static bool IsOurQuestion(string? question) => question is not null &&
        (question.StartsWith("Bạn muốn mức ", StringComparison.Ordinal) || question.StartsWith("Bạn muốn áp dụng khẩu vị ", StringComparison.Ordinal));

    private static string Dimension(string taste) => taste switch
    {
        "cay" => "spicy", "chua" => "sour", "ngot" => "sweet", "man" => "salty", "dang" => "bitter",
        "beo" => "fatty", "thanh mat" => "cooling", "chat" => "astringent", "tuoi" => "fresh",
        "dam da" => "rich", "thom" => "aromatic", _ => taste
    };

    private static string Normalize(string text)
    {
        var unquoted = Regex.Replace(text, "\"[^\"]*\"|'[^']*'|“[^”]*”|‘[^’]*’|`[^`]*`", " ");
        return new string(unquoted.Normalize(NormalizationForm.FormD)
            .Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark).ToArray())
            .Normalize(NormalizationForm.FormC).ToLowerInvariant().Replace('đ', 'd').Trim();
    }
}
