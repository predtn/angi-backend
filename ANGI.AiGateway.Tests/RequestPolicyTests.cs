using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace ANGI.AiGateway.Tests;

public sealed class RequestPolicyTests
{
    internal static byte[] Body(string? extra = null) => Encoding.UTF8.GetBytes(
        "{\"model\":\"gpt-6-luna\",\"messages\":[{\"role\":\"user\",\"content\":\"plan a food trip\"}]" + (extra is null ? "" : "," + extra) + "}");

    [Fact]
    public void InjectsOutputAndReasoningCapsAndReservesFullContext()
    {
        var request = new RequestPolicy(new()).Prepare("chat", Body());
        var json = JsonNode.Parse(request.Body)!;
        Assert.Equal(16000, json["max_completion_tokens"]!.GetValue<int>());
        Assert.Equal("none", json["reasoning_effort"]!.GetValue<string>());
        Assert.Equal("default", json["service_tier"]!.GetValue<string>());
        Assert.Equal(0.2745m, request.ReservedUsd);
    }

    [Theory]
    [InlineData("\"model\":\"expensive-model\"")]
    [InlineData("\"stream\":true")]
    [InlineData("\"n\":2")]
    [InlineData("\"service_tier\":\"priority\"")]
    [InlineData("\"max_completion_tokens\":16001")]
    [InlineData("\"max_completion_tokens\":0")]
    [InlineData("\"max_tokens\":1,\"max_completion_tokens\":1")]
    [InlineData("\"reasoning_effort\":\"high\"")]
    [InlineData("\"tools\":[{\"type\":\"web_search\"}]")]
    [InlineData("\"modalities\":[\"audio\"]")]
    [InlineData("\"messages\":[{\"role\":\"user\",\"content\":[{\"type\":\"image_url\"}]}]")]
    [InlineData("\"response_format\":{\"type\":\"json_object\",\"type\":\"text\"}")]
    [InlineData("\"max_tokens\":\"NaN\"")]
    public void UnsafeOrAmbiguousOptionsAreRejected(string extra) =>
        Assert.Throws<GatewayDenied>(() => new RequestPolicy(new()).Prepare("chat", Body(extra)));

    [Fact]
    public void RejectsOversizedInputAndNonChatWorkloads()
    {
        Assert.Throws<GatewayDenied>(() => new RequestPolicy(new()).Prepare("chat", new byte[196609]));
        Assert.Throws<GatewayDenied>(() => new RequestPolicy(new()).Prepare("admin", Body()));
        var removedWorkload = Assert.Throws<GatewayDenied>(() => new RequestPolicy(new()).Prepare("vectors", Body()));
        Assert.Equal(401, removedWorkload.Status);
    }

    [Fact]
    public void WrongModelAndImageContentAreRejectedWithoutDuplicateProperties()
    {
        var root = JsonNode.Parse(Body())!;
        root["model"] = "expensive-model";
        Assert.Throws<GatewayDenied>(() => new RequestPolicy(new()).Prepare("chat", JsonSerializer.SerializeToUtf8Bytes(root)));
        root["model"] = GatewaySettings.Model;
        root["messages"]![0]!["content"] = JsonNode.Parse("[{\"type\":\"image_url\",\"image_url\":{\"url\":\"https://example.com/image.png\"}}]");
        Assert.Throws<GatewayDenied>(() => new RequestPolicy(new()).Prepare("chat", JsonSerializer.SerializeToUtf8Bytes(root)));
    }

    [Fact]
    public void ChatFunctionToolsAndStrictSchemaRemainUsable()
    {
        var request = new RequestPolicy(new()).Prepare("chat", Body("\"tools\":[{\"type\":\"function\",\"function\":{\"name\":\"rank\",\"parameters\":{\"type\":\"object\"}}}],\"max_completion_tokens\":16000"));
        Assert.Equal(16000, request.MaxOutputTokens);
        Assert.Equal("none", JsonNode.Parse(request.Body)!["reasoning_effort"]!.GetValue<string>());
    }

    [Fact]
    public void Gpt6LunaRejectsFunctionToolsWithReasoningBeforeAdmission()
    {
        var body = Body("\"tools\":[{\"type\":\"function\",\"function\":{\"name\":\"rank\",\"parameters\":{\"type\":\"object\"}}}],\"reasoning_effort\":\"low\"");
        var error = Assert.Throws<GatewayDenied>(() => new RequestPolicy(new()).Prepare("chat", body));
        Assert.Equal("gateway_function_tools_require_no_reasoning", error.Code);
        Assert.Equal(400, error.Status);
    }

    [Fact]
    public void Gpt6LunaTextOnlyRequestsCanUseLowReasoning()
    {
        var request = new RequestPolicy(new()).Prepare("chat", Body("\"reasoning_effort\":\"low\""));
        Assert.Equal("low", JsonNode.Parse(request.Body)!["reasoning_effort"]!.GetValue<string>());
    }

    [Fact]
    public void PreviousModelCannotUseTheNewPricingPolicy()
    {
        var body = JsonNode.Parse(Body())!;
        body["model"] = "gpt-5.6-luna";
        var error = Assert.Throws<GatewayDenied>(() => new RequestPolicy(new()).Prepare("chat", JsonSerializer.SerializeToUtf8Bytes(body)));
        Assert.Equal("gateway_model_or_option_not_allowed", error.Code);
    }

    [Fact]
    public void ReasoningIsAlreadyIncludedInCompletionUsage()
    {
        var request = new RequestPolicy(new()).Prepare("chat", Body());
        var usage = RequestPolicy.ReadUsage(Encoding.UTF8.GetBytes("{\"usage\":{\"prompt_tokens\":100,\"completion_tokens\":50,\"total_tokens\":150,\"completion_tokens_details\":{\"reasoning_tokens\":30}}}"), request)!;
        Assert.Equal(30, usage.ReasoningTokens);
        Assert.Equal(RequestPolicy.Cost(100, 50), usage.ChargedUpperUsd);
    }

    [Fact]
    public void MissingReasoningBreakdownIsUnknownButStillCoveredByCompletionCost()
    {
        var request = new RequestPolicy(new()).Prepare("chat", Body());
        var usage = RequestPolicy.ReadUsage(Encoding.UTF8.GetBytes("{\"usage\":{\"prompt_tokens\":100,\"completion_tokens\":50}}"), request)!;
        Assert.Null(usage.ReasoningTokens);
        Assert.Equal(RequestPolicy.Cost(100, 50), usage.ChargedUpperUsd);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"usage\":null}")]
    [InlineData("{\"usage\":{\"prompt_tokens\":100,\"completion_tokens\":16001}}")]
    [InlineData("{\"usage\":{\"prompt_tokens\":-1,\"completion_tokens\":1}}")]
    [InlineData("{\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1,\"total_tokens\":3}}")]
    [InlineData("{\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1,\"completion_tokens_details\":{\"reasoning_tokens\":2}}}")]
    public void MissingOrUntrustworthyUsageCannotReleaseReservation(string body) =>
        Assert.Null(RequestPolicy.ReadUsage(Encoding.UTF8.GetBytes(body), new RequestPolicy(new()).Prepare("chat", Body())));

    [Fact]
    public void CredentialsSeparateChatAndAdministrationAndRejectVectorKeys()
    {
        var settings = new GatewaySettings { ChatKey = "angi-chat-" + new string('c', 40), AdminKey = "angi-admin-" + new string('a', 40) };
        settings.Validate();
        Assert.Equal("chat", settings.Authenticate("Bearer " + settings.ChatKey));
        Assert.Null(settings.Authenticate("Bearer angi-vectors-" + new string('v', 40)));
        Assert.Equal("admin", settings.Authenticate("Bearer " + settings.AdminKey));
        Assert.Null(settings.Authenticate("Bearer sk-provider-key"));
        Assert.Null(new GatewaySettings().Authenticate("Bearer "));
    }
}
