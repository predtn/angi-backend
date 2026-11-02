using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ANGI.Application.Common.Models.Agent;

namespace ANGI.Application.Services.Agent;

/// <summary>
/// Lets the model word a reply while the backend keeps the facts: wording is shown only when every number and proper name in it
/// is backed by trusted data. Anything else falls back to the fixed reply, so a failed check is invisible to the user.
/// </summary>
public static class AgentGroundedReply
{
    private const int MaxReplyLength = 500;
    private const int MaxOpenerLength = 60;
    private static readonly HashSet<string> GenericNames = ["angi", "ngay", "bua", "mon", "quan", "lich", "tong", "thu", "chu", "nhat", "viet", "nam"];
    // Cam kết thay đổi chỉ backend được nói sau khi đã kiểm tra; thuật ngữ nội bộ là dấu hiệu model đang lộ dữ liệu kỹ thuật.
    private static readonly Regex Forbidden = new(@"https?:|www\.|\b(?:da|vua|se) (?:luu|doi|thay|them|xoa|bo|cap nhat|chot|dat)\b|\b(?:catalog|roadmap|proposal|backend|schema|llm|api|prompt|tag|json|grant|itemkey)\b", RegexOptions.CultureInvariant);
    // Dữ liệu món không có các thông tin này, trừ khi câu trả lời đang nói rõ là chưa có.
    private static readonly Regex Unbacked = new(@"\b(?:mo cua|dong cua|noi tieng|ngon nhat|dong khach|halal|di ung|an toan|danh gia cao)\b", RegexOptions.CultureInvariant);
    private static readonly Regex Admits = new(@"\b(?:chua co|khong co|chua ro|khong ro) (?:thong tin|du lieu)\b", RegexOptions.CultureInvariant);
    private static readonly Regex Numbers = new(@"\d+(?:[.,]\d+)*(?<unit>\s*(?:k|nghin|ngan)\b)?", RegexOptions.CultureInvariant);

    /// <summary>Picks the visible reply for a clarify or answer turn: trusted notices first, then checked model wording, then the fixed reply.</summary>
    public static string Choose(AgentContext context, AgentOutput output)
    {
        if (output.Action == "clarify")
        {
            // Câu hỏi khẩu vị và mới lạ được máy trạng thái nhận diện theo đúng câu chữ; các câu còn lại dựa trên bằng chứng backend nên cũng giữ câu cố định.
            var worded = output.Clarification is "edit_scope" or "general" && context.ToolNotice is null && context.CatalogNotice is null &&
                context.Exchanges.Count == 0 && AgentReplyBuilder.TripDetailsQuestion(context.State) is null;
            return worded && output.Reply.TrimEnd().EndsWith('?') && IsGrounded(output.Reply, context) ? output.Reply.Trim()
                : AgentReplyBuilder.Clarify(context, output.Clarification);
        }
        var trusted = context.ToolNotice is not null || context.CatalogAnswer is not null || AgentChatPolicy.IsPlanViewRequest(context.Request.Content);
        return !trusted && IsGrounded(output.Reply, context) ? output.Reply.Trim() : AgentReplyBuilder.Answer(context);
    }

    /// <summary>A friendly opening clause for a new preview, or null. It runs before backend repairs, so it may state no fact at all.</summary>
    public static string? Opener(string? reply)
    {
        var text = (reply ?? "").Trim();
        if (text.Length is 0 or > MaxOpenerLength || text.Any(char.IsDigit) || !IsWellFormed(text) || text[^1] is not ('!' or '.')) return null;
        return Capitalized(text).Any() ? null : text;
    }

    /// <summary>True when the wording is well formed, makes no claim only the backend may make, and all its numbers and names are backed.</summary>
    public static bool IsGrounded(string? reply, AgentContext context)
    {
        var text = (reply ?? "").Trim();
        if (text.Length is 0 or > MaxReplyLength || !IsWellFormed(text)) return false;
        if (Unbacked.IsMatch(Plain(text)) && !Admits.IsMatch(Plain(text))) return false;
        var trusted = Trusted(context);
        var numbers = TrustedNumbers(context, trusted);
        foreach (Match match in Numbers.Matches(text))
        {
            var value = new string(match.Value.Where(char.IsDigit).ToArray());
            if (!numbers.Contains(match.Groups["unit"].Success ? value + "000" : value)) return false;
        }
        var names = Words(trusted).Split(' ').ToHashSet();
        return Capitalized(text).All(word => GenericNames.Contains(word) || names.Contains(word));
    }

    private static bool IsWellFormed(string text) =>
        // Tiếng Việt có dấu là dấu hiệu model trả lời đúng ngôn ngữ; câu tiếng Anh hay câu nháp không bao giờ tới người dùng.
        text.Any(character => character > 127 && char.IsLetter(character)) && !text.Any(character => char.IsControl(character) && character != '\n') &&
        text.IndexOfAny(['<', '>', '{', '}']) < 0 && !Forbidden.IsMatch(Plain(text));

    /// <summary>All text the reply may quote: the plan, the trip, tool results of this turn and what the user just wrote.</summary>
    private static string Trusted(AgentContext context)
    {
        var parts = new List<string?> { context.Request.Content, context.State.Province, context.State.Title };
        parts.AddRange(context.State.MealRequests.SelectMany(request => new[] { request.DishName, request.RestaurantName }));
        if (context.Source is { } source) parts.AddRange(source.Items.SelectMany(item => new[] { item.DishName, item.RestaurantName }));
        parts.AddRange(context.Exchanges.SelectMany(exchange => exchange.Results).Select(result => result.GetRawText()));
        return string.Join(' ', parts.Where(part => part is not null));
    }

    private static HashSet<string> TrustedNumbers(AgentContext context, string trusted)
    {
        var numbers = Numbers.Matches(trusted).Select(match => new string(match.Value.Where(char.IsDigit).ToArray())).ToHashSet();
        void Add(decimal? value) { if (value is { } amount) numbers.Add(amount.ToString("0", CultureInfo.InvariantCulture)); }
        Add(context.State.BudgetAmount); Add(context.State.MaxPricePerMeal); Add(context.State.NumDays);
        if (context.Source is { } source)
        {
            foreach (var item in source.Items) Add(item.Price);
            Add(source.Items.Sum(item => item.Price));
            foreach (var day in source.Items.GroupBy(item => item.DayNumber)) Add(day.Sum(item => item.Price));
            for (var count = 1; count <= Math.Max(source.Items.Length, source.State.NumDays ?? 0); count++) numbers.Add(count.ToString(CultureInfo.InvariantCulture));
        }
        for (var day = 1; day <= (context.State.NumDays ?? 0); day++) numbers.Add(day.ToString(CultureInfo.InvariantCulture));
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

    private static string Plain(string text) => new string(text.Normalize(NormalizationForm.FormD)
        .Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark).ToArray())
        .Normalize(NormalizationForm.FormC).ToLowerInvariant().Replace('đ', 'd');

    private static string Words(string text) => Regex.Replace(Regex.Replace(Plain(text), @"[^a-z0-9 ]+", " "), @"\s+", " ").Trim();
}
