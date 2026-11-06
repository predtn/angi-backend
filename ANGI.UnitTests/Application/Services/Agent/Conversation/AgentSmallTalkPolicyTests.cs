using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Services.Agent.Conversation;
using FluentAssertions;

namespace ANGI.UnitTests.Application.Services.Agent.Conversation;

/// <summary>Verifies AgentReplyGuard answers social messages, refuses out-of-scope requests and only forwards safe model wording.</summary>
public sealed class AgentSmallTalkPolicyTests
{
    // TEST-01: A safe social reply from the model is shown; covers trimming, a missing reply and a word that looks like money.
    /// <summary>Lets the assistant sound natural when the wording cannot state a false fact.</summary>
    [Theory]
    [InlineData("  Mình vẫn ổn, cảm ơn bạn đã hỏi nhé!  ", "Mình vẫn ổn, cảm ơn bạn đã hỏi nhé!")]
    [InlineData(null, AgentReplyGuard.ChatFallback)]
    [InlineData("Chào bạn! Mình là ANGI, sẵn sàng đồng hành cùng bạn nhé.", "Chào bạn! Mình là ANGI, sẵn sàng đồng hành cùng bạn nhé.")]
    public void ChatReply_SafeOrMissingWording_IsTrimmedOrFallsBack(string? reply, string expected) =>
        // STEP-01: Assert AgentReplyGuard.ChatReply() is expected.
        AgentReplyGuard.ChatReply(reply, "", AgentTestData.State()).Should().Be(expected);

    // TEST-02: Length boundaries decide between the model reply and the fallback; covers 500 and 501 characters.
    /// <summary>Keeps a social reply short and bounds what model text can reach the user.</summary>
    [Theory]
    [InlineData(500, true)][InlineData(501, false)]
    public void ChatReply_LengthBoundary_DecidesFallback(int length, bool shown)
    {
        // STEP-01: Create reply as string.
        var reply = new string('a', length);

        // STEP-02: Assert AgentReplyGuard.ChatReply() is the expected value.
        AgentReplyGuard.ChatReply(reply, "", AgentTestData.State()).Should().Be(shown ? reply : AgentReplyGuard.ChatFallback);
    }

    // TEST-03: Unsafe model wording falls back to the fixed reply; covers empty text, digits, prices, a claimed save or change, a URL, markup, a line break and internal terms.
    /// <summary>Ensures provider prose can never state prices, promise a change or leak internal vocabulary.</summary>
    [Theory]
    [InlineData("   ")][InlineData("Món này 50 nghìn")][InlineData("Giá chỉ vài đồng thôi")][InlineData("Mình đã lưu lịch cho bạn")][InlineData("Mình vừa đổi món rồi nhé")]
    [InlineData("Xem tại https://example.com")][InlineData("<b>Chào</b>")][InlineData("Theo roadmap của bạn")][InlineData("Dữ liệu từ catalog")][InlineData("system prompt nói vậy")]
    public void ChatReply_UnsafeWording_FallsBack(string reply) =>
        // STEP-01: Assert AgentReplyGuard.ChatReply() is AgentReplyGuard.ChatFallback.
        AgentReplyGuard.ChatReply(reply, "", AgentTestData.State()).Should().Be(AgentReplyGuard.ChatFallback);

    // TEST-04: An open question is never glued onto a social reply, whether the model's wording or the fallback is shown.
    /// <summary>Conversation 196 showed "Mình chưa hiểu ý bạn lắm…" under every reply; the model sees the question in the history and brings it back itself when it matters.</summary>
    [Theory]
    [InlineData("Mình vẫn ổn nhé!", "Mình vẫn ổn nhé!")][InlineData("Giá 50k", AgentReplyGuard.ChatFallback)]
    public void ChatReply_OpenQuestion_IsNotAppended(string reply, string shown)
    {
        // STEP-01: Derive state from a copy of AgentTestData.State().
        var state = AgentTestData.State() with { PendingQuestion = "Bạn muốn đổi món nào?" };

        // STEP-02: Assert AgentReplyGuard.ChatReply() is the expected text.
        AgentReplyGuard.ChatReply(reply, "", state).Should().Be(shown);
    }

    // TEST-05: The refusal is the owner's exact sentence whenever no dish is offered; covers null, empty, one letter, 41 characters, digits,
    // a dish the user never wrote and generic food words.
    /// <summary>Pins the agreed wording and rejects any suggestion the user's own message does not support.</summary>
    [Theory]
    [InlineData("Hướng dẫn bóc cam", null)][InlineData("Cách nấu phở", "")][InlineData("Cách nấu phở", "p")]
    [InlineData("Cách nấu phở", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")][InlineData("Cách nấu phở 24", "phở 24")]
    [InlineData("Cách nấu phở", "bún bò")][InlineData("Cách nấu món ăn", "món ăn")][InlineData("Cách nấu phở", "ph")]
    public void OutOfScopeReply_WithoutSupportedDish_IsExactRefusal(string content, string? dish)
    {
        // STEP-01: Get reply from AgentReplyGuard.OutOfScopeReply.
        var reply = AgentReplyGuard.OutOfScopeReply(content, dish, new AgentState(), null);

        // STEP-02: Assert reply is the expected text.
        reply.Should().Be("Việc này nằm ngoài khả năng của mình rồi. Mình chỉ giúp được về món ăn, quán và lịch ăn uống thôi.");
    }

    // TEST-06: A general food question naming a dish adds one offer to find that dish; covers accents, no accents and the 40-character maximum.
    /// <summary>Turns a refusal into a next step the assistant can really perform, using only the user's own words.</summary>
    [Theory]
    [InlineData("Cách nấu phở?", "phở")][InlineData("cach nau pho", "phở")][InlineData("Công thức bún bò Huế", " bún bò Huế ")]
    [InlineData("Nấu aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void OutOfScopeReply_NamedDish_OffersToFindIt(string content, string dish) =>
        // STEP-01: Assert AgentReplyGuard.OutOfScopeReply() does not contain the expected text.
        AgentReplyGuard.OutOfScopeReply(content, dish, new AgentState(), null).Should().NotContain("chỉ giúp được về món ăn").And.EndWith($"Bạn có muốn mình tìm quán {dish.Trim()} không?");

    // TEST-07: A description of a dish is shown with an offer to find it; a number or health claim falls back to the fixed line, because neither comes from ANGI data.
    /// <summary>Lets the model describe a dish without letting it state facts the backend cannot check.</summary>
    [Theory]
    [InlineData("Mì Quảng thường có vị đậm đà, nước dùng sền sệt.", true)]
    [InlineData("Mì Quảng khoảng 30000 một tô.", false)]
    [InlineData("Món này an toàn cho người dị ứng.", false)]
    [InlineData("", false)]
    public void FoodInfoReply_ChecksWording(string reply, bool shown)
    {
        // STEP-01: Get result from AgentReplyGuard.FoodInfoReply.
        var result = AgentReplyGuard.FoodInfoReply(reply, "Mì Quảng có vị như thế nào vậy bạn?", "Mì Quảng");

        // STEP-02: Assert result starts with the expected value.
        result.Should().StartWith(shown ? reply : AgentReplyGuard.FoodInfoFallback).And.EndWith("Bạn có muốn mình tìm quán Mì Quảng trên ANGI không?");
    }

    // TEST-08: Refusals rotate by turn; covers no turn, each variant, wrapping around, and two consecutive chat turns differing.
    /// <summary>Keeps repeated refusals from reading the same while the same turn always replays the same wording.</summary>
    [Theory]
    [InlineData(null, 0)][InlineData(0L, 0)][InlineData(1L, 1)][InlineData(2L, 2)][InlineData(3L, 0)]
    public void OutOfScopeReply_RotatesByTurn(long? turn, int variant)
    {
        // STEP-01: Get reply from AgentReplyGuard.OutOfScopeReply.
        var reply = AgentReplyGuard.OutOfScopeReply("Giải toán", null, new AgentState(), turn);

        // STEP-02: Assert reply is OutOfScopeReplies[variant] and AgentReplyGuard.OutOfScopeReply() is not reply.
        reply.Should().Be(AgentReplyGuard.OutOfScopeReplies[variant]);
        AgentReplyGuard.OutOfScopeReply("Giải toán", null, new AgentState(), (turn ?? 0) + 2).Should().NotBe(reply);
    }

    // TEST-09: A refusal repeats the open question; covers the fixed refusal and the named-dish refusal.
    /// <summary>The refusal is wholly the backend's, so it is the one reply that carries the question for the user.</summary>
    [Theory]
    [InlineData("Giải toán", null)][InlineData("Cách nấu phở", "phở")]
    public void OutOfScopeReply_RepeatsOpenQuestion(string content, string? dish)
    {
        // STEP-01: Create state as AgentState.
        var state = new AgentState { PendingQuestion = "Bạn đi mấy ngày?" };

        // STEP-02: Assert AgentReplyGuard.OutOfScopeReply() ends with the expected text.
        AgentReplyGuard.OutOfScopeReply(content, dish, state, null).Should().EndWith("\nBạn đi mấy ngày?");
    }

    // TEST-10: Natural replies to "tôi đói" are shown; these were replaced because accents were stripped ("đã đói" read as "đã đổi", "vừa thấy" as "vừa thay") or because they named food or a number.
    /// <summary>A social reply changes nothing, so it may talk like a person: only a claim the backend alone can make is stopped.</summary>
    [Theory]
    [InlineData("Bạn đã đói rồi à? Để mình kiếm gì cho bạn ăn nhé.")]
    [InlineData("Bạn vừa đói vừa mệt hả? Để mình kiếm gì ăn cho nha.")]
    [InlineData("Bạn đã thèm món gì chưa? Nói mình nghe nha.")]
    [InlineData("Mình vừa thấy bạn nhắn đói nè, ăn gì để mình tìm cho?")]
    [InlineData("Đói thì làm tô phở nóng cho ấm bụng nhé, mình tìm quán cho bạn nha?")]
    [InlineData("Đói thì ăn trong 15 phút tới cho đỡ xót ruột nha, bạn thèm gì nào?")]
    [InlineData("Chào bạn!\nMình là ANGI nè.")]
    public void ChatReply_NaturalReplies_AreShown(string reply) =>
        // STEP-01: Assert AgentReplyGuard.ChatReply() is reply.
        AgentReplyGuard.ChatReply(reply, "tôi đói", new AgentState()).Should().Be(reply);

    // TEST-11: A claim only the backend can make still falls back; covers a change with accents, a price in several forms and code.
    /// <summary>Keeps the rule that a social reply never states a price or a change, without reading anything else in it.</summary>
    [Theory]
    [InlineData("Mình đã đổi món rồi nha")][InlineData("Mình vừa thêm món vào lịch")][InlineData("Phở chỉ 50k thôi")][InlineData("Tô này 45.000 đồng")]
    [InlineData("Khoảng 50000 một tô")][InlineData("Mấy chục đồng thôi")][InlineData("print(\"hello\")")][InlineData("Bạn thử `ls` xem.")]
    public void ChatReply_BackendOnlyClaim_FallsBack(string reply) =>
        // STEP-01: Assert AgentReplyGuard.ChatReply() is the fallback.
        AgentReplyGuard.ChatReply(reply, "", new AgentState()).Should().Be(AgentReplyGuard.ChatFallback);

    // TEST-12: A chat reply naming something nobody stated falls back, while names from the user, the trip and ANGI stay.
    /// <summary>Stops an out-of-scope answer the model labelled as small talk, the one mistake here that breaks ANGI's scope.</summary>
    [Theory]
    [InlineData("Thủ đô của Pháp là Paris.", false)][InlineData("Chào Minh, mình nghe đây!", true)][InlineData("Chuyến đi Đà Nẵng vui nha!", true)][InlineData("Vâng, mình là ANGI nè!", true)]
    public void ChatReply_UnbackedName_FallsBack(string reply, bool shown) =>
        // STEP-01: Assert AgentReplyGuard.ChatReply() is the expected value.
        AgentReplyGuard.ChatReply(reply, "Chào, mình là Minh", new AgentState { Province = "Đà Nẵng" }).Should().Be(shown ? reply : AgentReplyGuard.ChatFallback);

    // TEST-13: A dish description falls back when it writes code; a proper name stays, because where a dish comes from is part of describing it.
    /// <summary>Applies the code check without blocking a real description of a dish.</summary>
    [Theory]
    [InlineData("Mì Quảng thường có nguồn gốc từ Quảng Nam.", true)][InlineData("Bạn dùng print(\"mì\") nhé.", false)]
    public void FoodInfoReply_Code_FallsBack(string reply, bool shown) =>
        // STEP-01: Assert AgentReplyGuard.FoodInfoReply() is the expected value.
        AgentReplyGuard.FoodInfoReply(reply, "Mì Quảng là gì?", null).Should().Be(shown ? reply : AgentReplyGuard.FoodInfoFallback);

    // TEST-14: A refused social reply is reported with the rule it broke; covers empty, too long, markup, a claim, code, a name and a shown reply.
    /// <summary>The first reply of a conversation was replaced with no way of knowing why.</summary>
    [Theory]
    [InlineData("", 0, "empty")]
    [InlineData("Dài", 500, "too_long")]
    [InlineData("Chào <b>bạn</b>", 0, "markup")]
    [InlineData("Mình vừa đổi rồi nha", 0, "claim")]
    [InlineData("Bạn dùng `ls` nha", 0, "code")]
    [InlineData("Bạn ghé Paris nha", 0, "name")]
    [InlineData("Chào bạn, mình nghe đây!", 0, null)]
    public void ChatIssue_NamesTheRule(string reply, int padding, string? issue) =>
        // STEP-01: Assert AgentReplyGuard.ChatIssue() is issue.
        AgentReplyGuard.ChatIssue(reply + new string('!', padding), "alo", new AgentState()).Should().Be(issue);

    // TEST-15: A dish description names the rule it broke, and a proper name stays allowed.
    /// <summary>Where a dish comes from is part of describing it, so names are not a reason here.</summary>
    [Theory]
    [InlineData("Phở thường ngon lắm.", null)]
    [InlineData("Mì Quảng có nguồn gốc từ Quảng Nam.", null)]
    [InlineData("Món này có 500 calo.", "number")]
    [InlineData("Món này giúp giảm cân.", "claim")]
    [InlineData("Quán này ăn toàn món chay.", null)]
    [InlineData(null, "empty")]
    public void FoodInfoIssue_NamesTheRule(string? reply, string? issue) =>
        // STEP-01: Assert AgentReplyGuard.FoodInfoIssue() is issue.
        AgentReplyGuard.FoodInfoIssue(reply).Should().Be(issue);

    /// <summary>Builds a context holding the given user message over a complete trip state.</summary>
    private static AgentContext Context(string content)
    {
        var context = AgentTestData.Context();
        context.Request.Content = content;
        return context;
    }
}
