using System.Net;
using System.Text;
using System.Text.Json;
using ANGI.Application.Common.Models.Agent;
using ANGI.Infrastructure.Services.Agent;
using ANGI.Infrastructure.Services.Ai;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace ANGI.UnitTests.Infrastructure.Services.Agent;

/// <summary>Verifies the dish-pick request, the reading of its answer, failures and the reuse of a repeated pick.</summary>
public sealed class OpenAiAgentDishPickerTests
{
    private static readonly AgentPickCandidate[] Candidates =
        [new(1, "Bún chả chấm", "Yến Bún Chả", 45000), new(2, "Ramen Shio", "OMA Nhà Hàng Nhật Bản", 79000), new(3, "Nem rán", "Yến Bún Chả", 5000)];

    // TEST-01: The list is grouped by restaurant and sent before the wish, so requests over the same list share a cached prompt; the answer's ids come back
    // as the model wrote them, and the same wish over the same list is answered from memory without a second call.
    /// <summary>Sends a strict schema, reads best and good, and reuses a repeated pick.</summary>
    [Fact]
    public async Task PickAsync_ReadsPicksAndReusesRepeatedRequest()
    {
        string? prompt = null;
        var handler = new Handler(async request =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            body.RootElement.GetProperty("response_format").GetProperty("json_schema").GetProperty("strict").GetBoolean().Should().BeTrue();
            prompt = body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString();
            return Response("stop", new { best = new[] { 1, 99 }, good = new[] { 3 } });
        });
        var picker = Picker(handler);
        var request = new AgentDishPickRequest("món đặc trưng Hà Nội", Candidates, 5);

        var first = await picker.PickAsync(request, CancellationToken.None);
        var again = await picker.PickAsync(request, CancellationToken.None);

        first!.Best.Should().Equal(1, 99);
        first.Good.Should().Equal(3);
        again!.Best.Should().Equal(1, 99);
        handler.Calls.Should().Be(1);
        prompt.Should().Contain("# Yến Bún Chả\n1 Bún chả chấm 45k; 3 Nem rán 5k");
        prompt!.IndexOf("Yến Bún Chả", StringComparison.Ordinal).Should().BeLessThan(prompt.IndexOf("món đặc trưng Hà Nội", StringComparison.Ordinal));
    }

    // TEST-02: A provider error, a cut-off answer or malformed JSON gives null, so the caller ranks without the wish; nothing is cached for them.
    /// <summary>Returns null for a 500, finish_reason=length and invalid JSON content.</summary>
    [Theory]
    [InlineData(500, "stop", "{\"best\":[1],\"good\":[]}")]
    [InlineData(200, "length", "{\"best\":[1],\"good\":[]}")]
    [InlineData(200, "stop", "not json")]
    public async Task PickAsync_Failure_ReturnsNull(int status, string finish, string content)
    {
        var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = finish, message = new { content } } } }), Encoding.UTF8, "application/json")
        }));
        var picker = Picker(handler);

        (await picker.PickAsync(new("món lạ", Candidates, 3), CancellationToken.None)).Should().BeNull();
        (await picker.PickAsync(new("món lạ", Candidates, 3), CancellationToken.None)).Should().BeNull();
        handler.Calls.Should().Be(2);
    }

    private static OpenAiAgentDishPicker Picker(Handler handler) => new(new HttpClient(handler) { BaseAddress = new("https://api.example.test/v1/") },
        Options.Create(new AiSettings { ApiKey = "test-key", Model = "gpt-6-luna" }), new MemoryCache(new MemoryCacheOptions()));

    private static HttpResponseMessage Response(string finish, object picks) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new
        {
            choices = new[] { new { finish_reason = finish, message = new { content = JsonSerializer.Serialize(picks) } } },
            usage = new { total_tokens = 120 }
        }), Encoding.UTF8, "application/json")
    };

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Interlocked.Increment(ref Calls); return send(request); }
    }
}
