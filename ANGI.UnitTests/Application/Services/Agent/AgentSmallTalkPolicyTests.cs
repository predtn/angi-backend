using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Services.Agent;
using FluentAssertions;

namespace ANGI.UnitTests.Application.Services.Agent;

/// <summary>Verifies AgentSmallTalkPolicy answers social messages, refuses out-of-scope requests and only forwards safe model wording.</summary>
public sealed class AgentSmallTalkPolicyTests
{
    // TEST-01: Whole-message greetings get the fixed self-introduction; covers accents, no accents, punctuation, casing, English and addressees.
    /// <summary>Ensures "xin chào" is answered by introducing the assistant without spending a provider call.</summary>
    [Theory]
    [InlineData("Xin chào")][InlineData("xin chao")][InlineData("Chào bạn!")][InlineData("chao ban")][InlineData("HELLO")]
    [InlineData("hi angi")][InlineData("Alo")][InlineData("Chào buổi sáng")][InlineData("  xin chào ạ  ")]
    public void FixedReply_Greeting_ReturnsSelfIntroduction(string content)
    {
        AgentSmallTalkPolicy.FixedReply(Context(content)).Should().Be(AgentSmallTalkPolicy.GreetingFor(AgentTestData.State()));
        AgentSmallTalkPolicy.GreetingFor(AgentTestData.State()).Should().StartWith("Chào bạn, mình là ANGI").And.EndWith("chuyến đi Da Nang?");
    }

    // TEST-02: Thanks and identity questions get their own fixed reply; covers each wording group.
    /// <summary>Ensures common social messages stay free of provider calls and never list the plan.</summary>
    [Theory]
    [InlineData("Cảm ơn", AgentSmallTalkPolicy.Thanks)][InlineData("cam on ban nhieu", AgentSmallTalkPolicy.Thanks)][InlineData("Thanks!", AgentSmallTalkPolicy.Thanks)]
    [InlineData("Bạn là ai?", AgentSmallTalkPolicy.Introduction)][InlineData("ANGI là gì vậy", AgentSmallTalkPolicy.Introduction)][InlineData("bạn làm được gì", AgentSmallTalkPolicy.Introduction)]
    public void FixedReply_ThanksAndIdentity_ReturnFixedWording(string content, string expected) =>
        AgentSmallTalkPolicy.FixedReply(Context(content)).Should().Be(expected);

    // TEST-03: Anything beyond a bare social message goes to the model; covers a planning request after a greeting, a dish spelled like a greeting, empty text and off-topic text.
    /// <summary>Prevents a real request or the dish "cháo" from being swallowed by a fixed greeting.</summary>
    [Theory]
    [InlineData("Xin chào, lập lịch 3 ngày ở Huế")][InlineData("cháo")][InlineData("Cháo bạn")][InlineData("")][InlineData("   ")][InlineData("Hướng dẫn bóc cam")][InlineData("chào mừng")]
    public void FixedReply_OtherMessages_ReturnNull(string content) =>
        AgentSmallTalkPolicy.FixedReply(Context(content)).Should().BeNull();

    // TEST-04: A greeting does not bypass constraint confirmation, a replan or a UI command; covers confirmedState, replan, alternative and the chat command.
    /// <summary>Ensures those turns keep going through the flows that validate them.</summary>
    [Theory]
    [InlineData("confirmed", false)][InlineData("replan", false)][InlineData("alternative", false)][InlineData("chat", true)]
    public void FixedReply_GuardedTurns_DoNotShortCircuit(string kind, bool answered)
    {
        var context = Context("Xin chào");
        if (kind == "confirmed") context.Request.ConfirmedState = AgentTestData.State();
        if (kind == "replan") context.ReplanAfterConfirmation = true;
        if (kind is "alternative" or "chat") context.Request.Command = kind;

        (AgentSmallTalkPolicy.FixedReply(context) is not null).Should().Be(answered);
    }

    // TEST-05: A greeting reminds the user of the question still open, so the conversation does not silently stall.
    /// <summary>The question remains unanswered and keeps blocking save, so the reply must repeat it.</summary>
    [Fact]
    public void FixedReply_OpenQuestion_IsRepeatedAfterGreeting()
    {
        var context = Context("Xin chào");
        context.State = context.State with { PendingQuestion = "Bạn muốn đổi món nào?" };

        AgentSmallTalkPolicy.FixedReply(context).Should().Be($"{AgentSmallTalkPolicy.GreetingFor(context.State)}\nBạn muốn đổi món nào?");
    }

    // TEST-06: A safe social reply from the model is shown; covers trimming and the 200-character maximum.
    /// <summary>Lets the assistant sound natural when the wording cannot state a false fact.</summary>
    [Theory]
    [InlineData("  Mình vẫn ổn, cảm ơn bạn đã hỏi nhé!  ", "Mình vẫn ổn, cảm ơn bạn đã hỏi nhé!")]
    [InlineData(null, AgentSmallTalkPolicy.ChatFallback)]
    public void ChatReply_SafeOrMissingWording_IsTrimmedOrFallsBack(string? reply, string expected) =>
        AgentSmallTalkPolicy.ChatReply(reply, AgentTestData.State()).Should().Be(expected);

    // TEST-07: Length boundaries decide between the model reply and the fallback; covers 200 and 201 characters.
    /// <summary>Keeps a social reply short and bounds what model text can reach the user.</summary>
    [Theory]
    [InlineData(200, true)][InlineData(201, false)]
    public void ChatReply_LengthBoundary_DecidesFallback(int length, bool shown)
    {
        var reply = new string('a', length);

        AgentSmallTalkPolicy.ChatReply(reply, AgentTestData.State()).Should().Be(shown ? reply : AgentSmallTalkPolicy.ChatFallback);
    }

    // TEST-08: Unsafe model wording falls back to the fixed reply; covers empty text, digits, prices, a claimed save or change, a URL, markup, a line break and internal terms.
    /// <summary>Ensures provider prose can never state prices, promise a change or leak internal vocabulary.</summary>
    [Theory]
    [InlineData("   ")][InlineData("Món này 50 nghìn")][InlineData("Giá chỉ vài đồng thôi")][InlineData("Mình đã lưu lịch cho bạn")][InlineData("Mình vừa đổi món rồi nhé")]
    [InlineData("Xem tại https://example.com")][InlineData("<b>Chào</b>")][InlineData("Chào\nbạn")][InlineData("Theo roadmap của bạn")][InlineData("Dữ liệu từ catalog")][InlineData("system prompt nói vậy")]
    public void ChatReply_UnsafeWording_FallsBack(string reply) =>
        AgentSmallTalkPolicy.ChatReply(reply, AgentTestData.State()).Should().Be(AgentSmallTalkPolicy.ChatFallback);

    // TEST-09: An open question is repeated after a model-written social reply and after the fallback.
    /// <summary>Keeps the reminder independent of whether the model wording was accepted.</summary>
    [Theory]
    [InlineData("Mình vẫn ổn nhé!", "Mình vẫn ổn nhé!")][InlineData("Giá 50k", AgentSmallTalkPolicy.ChatFallback)]
    public void ChatReply_OpenQuestion_IsRepeated(string reply, string shown)
    {
        var state = AgentTestData.State() with { PendingQuestion = "Bạn muốn đổi món nào?" };

        AgentSmallTalkPolicy.ChatReply(reply, state).Should().Be($"{shown}\nBạn muốn đổi món nào?");
    }

    // TEST-10: The refusal is the owner's exact sentence whenever no dish is offered; covers null, empty, one letter, 41 characters, digits,
    // a dish the user never wrote and generic food words.
    /// <summary>Pins the agreed wording and rejects any suggestion the user's own message does not support.</summary>
    [Theory]
    [InlineData("Hướng dẫn bóc cam", null)][InlineData("Cách nấu phở", "")][InlineData("Cách nấu phở", "p")]
    [InlineData("Cách nấu phở", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")][InlineData("Cách nấu phở 24", "phở 24")]
    [InlineData("Cách nấu phở", "bún bò")][InlineData("Cách nấu món ăn", "món ăn")][InlineData("Cách nấu phở", "ph")]
    public void OutOfScopeReply_WithoutSupportedDish_IsExactRefusal(string content, string? dish)
    {
        var reply = AgentSmallTalkPolicy.OutOfScopeReply(content, dish);

        reply.Should().Be("Việc này nằm ngoài khả năng của mình rồi. Mình chỉ giúp được về món ăn, quán và lịch ăn uống thôi.");
    }

    // TEST-11: A general food question naming a dish adds one offer to find that dish; covers accents, no accents and the 40-character maximum.
    /// <summary>Turns a refusal into a next step the assistant can really perform, using only the user's own words.</summary>
    [Theory]
    [InlineData("Cách nấu phở?", "phở")][InlineData("cach nau pho", "phở")][InlineData("Công thức bún bò Huế", " bún bò Huế ")]
    [InlineData("Nấu aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void OutOfScopeReply_NamedDish_OffersToFindIt(string content, string dish) =>
        AgentSmallTalkPolicy.OutOfScopeReply(content, dish).Should().Be($"{AgentSmallTalkPolicy.OutOfScope} Bạn có muốn mình tìm quán {dish.Trim()} không?");

    // TEST-12: A greeting names the trip when its place is known and stays general otherwise; covers a known province, null and blank.
    /// <summary>The reply should follow this conversation; an unknown place is never guessed.</summary>
    [Theory]
    [InlineData("Đà Nẵng", "Chào bạn, mình là ANGI, trợ lý ăn uống của bạn. Bạn muốn mình giúp gì cho chuyến đi Đà Nẵng?")]
    [InlineData(null, AgentSmallTalkPolicy.Greeting)]
    [InlineData("  ", AgentSmallTalkPolicy.Greeting)]
    public void GreetingFor_UsesKnownPlaceOnly(string? province, string expected) =>
        AgentSmallTalkPolicy.GreetingFor(new AgentState { Province = province }).Should().Be(expected);

    /// <summary>Builds a context holding the given user message over a complete trip state.</summary>
    private static AgentContext Context(string content)
    {
        var context = AgentTestData.Context();
        context.Request.Content = content;
        return context;
    }
}
