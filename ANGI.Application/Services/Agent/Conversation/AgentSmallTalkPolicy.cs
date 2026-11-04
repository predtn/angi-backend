using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ANGI.Application.Common.Models.Agent;

namespace ANGI.Application.Services.Agent.Conversation;

/// <summary>Answers social messages and refuses requests outside ANGI; neither can touch the plan, state or an open question.</summary>
public static class AgentSmallTalkPolicy
{
    public const string Greeting = "Chào bạn, mình là ANGI, trợ lý ăn uống của bạn. Bạn muốn tìm món, tìm quán hay lập lịch ăn uống nào?";
    public const string Introduction = "Mình là ANGI, trợ lý ăn uống của bạn. Mình giúp bạn tìm món, tìm quán và lập lịch ăn uống cho chuyến đi.";
    public const string Help = "Mình có thể lập lịch ăn uống cho chuyến đi, tìm món hoặc quán trên ANGI và đổi món trong lịch của bạn. Bạn thử nhắn “lập lịch cho tôi” để bắt đầu nhé.";
    public const string Thanks = "Không có gì nha! Cần đổi món hay tìm quán nào cứ nhắn mình.";
    public const string NotUnderstood = "Mình chưa hiểu rõ ý bạn. Bạn muốn lập lịch ăn uống, tìm món, tìm quán hay đổi món trong lịch?";
    public const string ChatFallback = "Mình luôn sẵn sàng! Bạn muốn tìm món, tìm quán hay lập lịch ăn uống nào?";
    // Câu từ chối do chủ dự án chốt: giữ nguyên văn, không để model viết lại.
    public const string OutOfScope = "Việc này nằm ngoài khả năng của mình rồi. Mình chỉ giúp được về món ăn, quán và lịch ăn uống thôi.";
    private const int MaxChatReplyLength = 200;
    private const int MaxDishLength = 40;

    private const string Addressee = @"(?: (?:ban|angi|ad|admin|em|anh|chi|tro ly|buoi sang|buoi trua|buoi chieu|buoi toi|nhieu|lam|nhe|nha|a|nhe ban|nha ban))*";
    private static readonly Regex GreetingWords = new(@"^(?:(?:xin )?chao|hi|hello|helo|hey|alo)" + Addressee + "$", RegexOptions.CultureInvariant);
    private static readonly Regex ThanksWords = new(@"^(?:(?:xin |rat |da )?cam on|thanks?(?: you)?|tks|thx)" + Addressee + "$", RegexOptions.CultureInvariant);
    private static readonly Regex IntroductionWords = new(
        @"^(?:(?:ban|em|angi) la (?:ai|gi)|(?:ban|em|angi)(?: co the)? lam(?: duoc)? (?:nhung )?gi)(?: (?:vay|the|day|nhi|a|cho toi|cho minh))*$", RegexOptions.CultureInvariant);
    // Chỉ lời nhờ trống ("hướng dẫn tôi", "giúp mình với"). Có chủ đề ("hướng dẫn tôi gọt vỏ cam") thì model phân loại như thường.
    private static readonly Regex HelpWords = new(
        @"^(?:(?:hay|xin|lam on|ban) )?(?:huong dan|giup(?: do)?|ho tro|chi dan|help|chi (?:toi|minh|em))(?: (?:toi|minh|em|voi|nhe|nha|di|cach dung|cach su dung|su dung))*$", RegexOptions.CultureInvariant);
    // Cam kết thay đổi và số liệu chỉ backend được nói; thuật ngữ nội bộ trong câu chào là dấu hiệu model đang lộ prompt.
    private static readonly Regex UnsafeChatWords = new(
        @"\d|https?:|www\.|\b(?:da|vua) (?:luu|doi|thay|them|xoa|bo|cap nhat|chot|dat)\b|\b(?:dong|vnd|usd)\b|\b(?:catalog|roadmap|proposal|backend|schema|llm|api|prompt|tag)\b", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> GenericFoodWords = ["mon", "mon an", "do an", "an", "quan", "quan an", "thuc an", "am thuc"];

    /// <summary>Returns the fixed reply for a whole-message greeting, thanks or "who are you", or null so the model reads the message.</summary>
    public static string? FixedReply(AgentContext context)
    {
        // Lượt xác nhận ràng buộc hoặc lệnh giao diện phải đi qua luồng kiểm tra của chúng, kể cả khi nội dung trông như lời chào.
        if (context.ReplanAfterConfirmation || context.Request.ConfirmedState is not null || context.Request.Command != "chat") return null;
        var text = Words(context.Request.Content);
        if (GreetingWords.IsMatch(text) && !NamesPorridge(context.Request.Content)) return WithOpenQuestion(GreetingFor(context.State), context.State);
        if (ThanksWords.IsMatch(text)) return WithOpenQuestion(Thanks, context.State);
        if (HelpWords.IsMatch(text)) return WithOpenQuestion(Help, context.State);
        return IntroductionWords.IsMatch(text) ? WithOpenQuestion(Introduction, context.State) : null;
    }

    /// <summary>Greets within the current trip when its place is known, so the reply follows this conversation rather than a fixed line.</summary>
    public static string GreetingFor(AgentState state) => string.IsNullOrWhiteSpace(state.Province) ? Greeting
        : $"Chào bạn, mình là ANGI, trợ lý ăn uống của bạn. Bạn muốn mình giúp gì cho chuyến đi {AgentReplyBuilder.Safe(state.Province)}?";

    /// <summary>Shows the model's social reply only when it states no fact, price, change or internal term; otherwise a fixed one.</summary>
    public static string ChatReply(string? reply, AgentState state)
    {
        var text = (reply ?? "").Trim();
        var safe = text.Length is > 0 and <= MaxChatReplyLength && !text.Any(char.IsControl) && text.IndexOfAny(['<', '>']) < 0 && !UnsafeChatWords.IsMatch(Words(text, keepSymbols: true));
        return WithOpenQuestion(safe ? text : ChatFallback, state);
    }

    /// <summary>Returns the fixed refusal, plus an offer to find a dish only when the user's own message names that dish.</summary>
    public static string OutOfScopeReply(string content, string? suggestedDish)
    {
        var dish = (suggestedDish ?? "").Trim();
        var named = dish.Length is >= 2 and <= MaxDishLength && Regex.IsMatch(dish, @"^[\p{L}\p{M} ]+$", RegexOptions.CultureInvariant) &&
            !GenericFoodWords.Contains(Words(dish)) && $" {Words(content)} ".Contains($" {Words(dish)} ", StringComparison.Ordinal);
        return named ? $"{OutOfScope} Bạn có muốn mình tìm quán {dish} không?" : OutOfScope;
    }

    // Câu đang chờ trả lời vẫn còn hiệu lực sau lời chào, nên nhắc lại để người dùng biết cuộc trò chuyện đang dừng ở đâu.
    private static string WithOpenQuestion(string reply, AgentState state) =>
        state.PendingQuestion is { } question ? $"{reply}\n{question}" : reply;

    // Bỏ dấu biến "cháo" (món ăn) thành "chao" (lời chào); câu có dấu phải viết đúng "chào".
    private static bool NamesPorridge(string content) => Regex.Split(content.Normalize(NormalizationForm.FormC).ToLowerInvariant(), @"[^\p{L}]+")
        .Any(word => Words(word) == "chao" && word is not ("chào" or "chao"));

    private static string Words(string text, bool keepSymbols = false)
    {
        var plain = new string(text.Normalize(NormalizationForm.FormD)
            .Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark).ToArray())
            .Normalize(NormalizationForm.FormC).ToLowerInvariant().Replace('đ', 'd');
        return keepSymbols ? plain : Regex.Replace(Regex.Replace(plain, @"[^a-z0-9 ]+", " "), @"\s+", " ").Trim();
    }
}
