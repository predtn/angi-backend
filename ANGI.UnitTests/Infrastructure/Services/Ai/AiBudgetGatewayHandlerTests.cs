using System.Net;
using System.Net.Http.Headers;
using ANGI.Infrastructure.Services.Ai;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ANGI.UnitTests.Infrastructure.Services.Ai;

public sealed class AiBudgetGatewayHandlerTests
{
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

    private static HttpClient Client(RecordingTransport transport, string? address = null) => new(new AiBudgetGatewayHandler(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["AiBudgetGateway:BaseUrl"] = address }).Build()) { InnerHandler = transport });
    private static HttpRequestMessage Request(string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
        if (token.Length > 0) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }
    private sealed class RecordingTransport : HttpMessageHandler
    {
        public int Calls; public Uri? Target;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Calls++; Target = request.RequestUri; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); }
    }
}
