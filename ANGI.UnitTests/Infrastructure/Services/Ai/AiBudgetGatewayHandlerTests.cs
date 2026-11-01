using System.Net;
using System.Net.Http.Headers;
using ANGI.Infrastructure.Services.Ai;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ANGI.UnitTests.Infrastructure.Services.Ai;

/// <summary>Verifies AiBudgetGatewayHandler forces every AI call through the budget gateway with a correctly scoped key.</summary>
public sealed class AiBudgetGatewayHandlerTests
{
    // TEST-01: Raw provider keys and wrongly scoped keys are blocked with 503 before reaching the transport; covers an empty token, a raw provider key, a vectors-scope key and a too-short chat key.
    /// <summary>Prevents any request from bypassing the budget guard with a raw or wrongly scoped key.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("sk-provider-real-key-would-bypass-budget-guard")]
    [InlineData("angi-vectors-token-cannot-use-backend-chat-scope")]
    [InlineData("angi-chat-too-short")]
    public async Task RawProviderAndWrongScopedKeysAreBlockedBeforeTransport(string token)
    {
        var transport = new RecordingTransport();
        using var client = Client(transport);
        using var request = Request(token);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(0, transport.Calls);
    }

    // TEST-02: A legacy provider URL is always rewritten to the budget gateway chat endpoint.
    /// <summary>Ensures old provider URLs cannot reach the provider directly.</summary>
    [Fact]
    public async Task LegacyProviderUrlIsAlwaysRewrittenToBudgetGateway()
    {
        var transport = new RecordingTransport(); using var client = Client(transport);
        using var request = Request("angi-chat-" + new string('c', 40));
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("http://127.0.0.1:18080/v1/chat/completions", transport.Target!.ToString());
        Assert.Equal(1, transport.Calls);
    }

    // TEST-03: An invalid gateway address never reaches the transport and returns 503; covers a query string, embedded credentials and a file URL.
    /// <summary>Prevents a malformed gateway address from leaking requests or credentials.</summary>
    [Theory]
    [InlineData("http://gateway/v1/?redirect=evil")]
    [InlineData("http://key@gateway/v1/")]
    [InlineData("file:///tmp/v1/")]
    public async Task InvalidGatewayAddressCannotReachTransport(string address)
    {
        var transport = new RecordingTransport(); using var client = Client(transport, address);
        using var request = Request("angi-chat-" + new string('c', 40));
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode); Assert.Equal(0, transport.Calls);
    }

    /// <summary>Creates an HttpClient that runs the handler over the recording transport with an optional gateway address.</summary>
    private static HttpClient Client(RecordingTransport transport, string? address = null) => new(new AiBudgetGatewayHandler(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["AiBudgetGateway:BaseUrl"] = address }).Build()) { InnerHandler = transport });
    /// <summary>Creates a chat completion request to the legacy provider URL, with a bearer token when one is given.</summary>
    private static HttpRequestMessage Request(string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
        if (token.Length > 0) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }
    /// <summary>Inner handler that counts calls and records the final target URL instead of sending anything.</summary>
    private sealed class RecordingTransport : HttpMessageHandler
    {
        public int Calls; public Uri? Target;
        /// <summary>Records the call and its target and answers 200 without network access.</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Calls++; Target = request.RequestUri; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); }
    }
}
