using System.Net;
using System.Text;
using System.Text.Json;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Services.Agent.Conversation;
using ANGI.Infrastructure.Services.Agent;
using ANGI.Infrastructure.Services.Ai;
using ANGI.UnitTests.Application.Services.Agent;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace ANGI.UnitTests.Infrastructure.Services.Agent;

/// <summary>Verifies the real provider transport, strict schemas, tool associations and malformed-output handling.</summary>
public sealed class OpenAiAgentLlmServiceTests
{
    // TEST-01: A business repair resends the rejected selection, validation feedback, proposal mode/version and issued candidate scopes.
    /// <summary>Gives the single proposal repair the exact data needed to correct the previous draft.</summary>
    [Fact]
    public async Task RunAsync_BusinessRepair_PreservesRejectedSelectionAndBackendMode()
    {
        // STEP-01: Get context from AgentTestData.Context, set context.ProposalVersion to 3, add the computed value to context.Grants and 2 more.
        var context = AgentTestData.Context(AgentTestData.Snapshot(AgentTestData.Item()));
        context.ProposalVersion = 3;
        context.Grants.Add(new(1, "breakfast", [221], Guid.NewGuid(), context.State));
        context.RejectedOutput = new AgentOutput { Action = "create_proposal", Reply = "Draft", Items = [AgentTestData.Item(999)] };
        context.RepairFeedback = "Món 999 cần nguồn ứng viên mới đúng phạm vi và khẩu vị.";
        // STEP-02: Assert state.RootElement.proposalMode is "update", state.RootElement.proposalVersion is 3, issued.dayNumber is 1 and 5 more.
        var handler = new Handler(async request =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var messages = body.RootElement.GetProperty("messages");
            using var state = JsonDocument.Parse(messages[1].GetProperty("content").GetString()!);
            state.RootElement.GetProperty("proposalMode").GetString().Should().Be("update");
            state.RootElement.GetProperty("proposalVersion").GetInt32().Should().Be(3);
            var issued = state.RootElement.GetProperty("issuedCandidateScopes")[0];
            issued.GetProperty("dayNumber").GetInt32().Should().Be(1);
            issued.GetProperty("mealSlot").GetString().Should().Be("breakfast");
            issued.GetProperty("dishIds")[0].GetInt32().Should().Be(221);
            var rejected = messages.EnumerateArray().Single(message => message.GetProperty("role").GetString() == "assistant");
            AgentJson.Read<AgentOutput>(rejected.GetProperty("content").GetString()!).Items.Single().DishId.Should().Be(999);
            messages[messages.GetArrayLength() - 1].GetProperty("content").GetString().Should().Contain("Món 999");
            return Response(new { choices = new[] { new { finish_reason = "stop", message = new { content = Output() } } } });
        });

        (await Service(handler).RunAsync(context, true, CancellationToken.None))!.Output.Should().NotBeNull();
    }

    // TEST-02: After the tool-round budget is spent, the request sets tool_choice=none and accepts a structured final output.
    /// <summary>Prevents another tool round once the configured round budget is exhausted.</summary>
    [Fact]
    public async Task RunAsync_ToolBudgetExhausted_ForcesFinalStructuredOutput()
    {
        // STEP-01: Assert sent.RootElement.tool_choice is "none".
        var handler = new Handler(async request =>
        {
            using var sent = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            sent.RootElement.GetProperty("tool_choice").GetString().Should().Be("none");
            return Response(new { choices = new[] { new { finish_reason = "stop", message = new { content = Output() } } } });
        });
        // STEP-02: Get context from AgentTestData.Context and repeat add new([], []) to context.Exchanges while round < 3.
        var context = AgentTestData.Context();
        for (var round = 0; round < 3; round++) context.Exchanges.Add(new([], []));
        // STEP-03: Assert Service().RunAsync().Output.Action is "answer".
        (await Service(handler).RunAsync(context, false, CancellationToken.None))!.Output!.Action.Should().Be("answer");
    }

    // TEST-03: Send allowlisted strict tool schemas and parse a complete structured answer with token usage.
    /// <summary>Verifies the exact HTTP authentication and structured-output contract without a live API key.</summary>
    [Fact]
    public async Task RunAsync_StrictAnswer_UsesExpectedProviderContract()
    {
        // STEP-01: Declare body to capture a call argument and get output from Output.
        string? body = null;
        var output = Output();
        // STEP-02: Assert request.Headers.Authorization.Scheme is "Bearer", request.Headers.Authorization.Parameter is "test-key" and 1 more.
        var handler = new Handler(async request =>
        {
            request.Headers.Authorization!.Scheme.Should().Be("Bearer"); request.Headers.Authorization.Parameter.Should().Be("test-key");
            request.RequestUri!.AbsolutePath.Should().Be("/v1/chat/completions");
            body = await request.Content!.ReadAsStringAsync();
            return Response(new { choices = new[] { new { finish_reason = "stop", message = new { content = output, refusal = (string?)null } } }, usage = new { total_tokens = 123 } });
        });
        // STEP-03: Get result from Service().RunAsync.
        var result = await Service(handler).RunAsync(AgentTestData.Context(), false, CancellationToken.None);
        // STEP-04: Assert result.Output.Action is "answer" and result.Tokens is 123.
        result!.Output!.Action.Should().Be("answer"); result.Tokens.Should().Be(123);
        // STEP-05: Read sent from the response; assert sent.RootElement.tools.length is 5, RootElement.response_format.json_schema.strict is true and 2 more.
        using var sent = JsonDocument.Parse(body!);
        sent.RootElement.GetProperty("tools").GetArrayLength().Should().Be(6);
        sent.RootElement.GetProperty("response_format").GetProperty("json_schema").GetProperty("strict").GetBoolean().Should().BeTrue();
        sent.RootElement.GetProperty("max_completion_tokens").GetInt32().Should().Be(16000);
        sent.RootElement.TryGetProperty("reasoning_effort", out _).Should().BeFalse();
        // STEP-06: Assert body does not contain "test-key".
        body.Should().NotContain("test-key");
    }

    // TEST-04: Requests after recommendation results use the configured completion budget and optional reasoning effort; covers no configured effort and low effort.
    /// <summary>Reserves the configured completion budget after recommendation results and preserves optional provider controls.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("low")]
    public async Task RunAsync_AfterRecommendation_UsesConfiguredCompletionBudget(string? effort)
    {
        // STEP-01: Assert root.max_completion_tokens is 12000, root.TryGetProperty() is effort is not null, reasoning is effort and 1 more.
        var handler = new Handler(async request =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var root = body.RootElement;
            root.GetProperty("max_completion_tokens").GetInt32().Should().Be(12000);
            root.TryGetProperty("reasoning_effort", out var reasoning).Should().Be(effort is not null);
            if (effort is not null) reasoning.GetString().Should().Be(effort);
            root.GetProperty("messages")[3].GetProperty("tool_call_id").GetString().Should().Be("call-reco");
            return Response(new { choices = new[] { new { finish_reason = "stop", message = new { content = Output() } } } });
        });
        // STEP-02: Get context from AgentTestData.Context, add the computed value to context.Exchanges and get result from Service().RunAsync.
        var context = AgentTestData.Context();
        context.Exchanges.Add(new([new("call-reco", "get_recommendation", JsonSerializer.SerializeToElement(new { }))],
            [JsonSerializer.SerializeToElement(new { rankingMode = "personalized", items = new[] { new { dishId = 123, name = "Món cay" } } })]));
        var result = await Service(handler, limits: new() { MaxOutputTokens = 12000, ReasoningEffort = effort })
            .RunAsync(context, false, CancellationToken.None);
        // STEP-03: Assert result.Output.Action is "answer" and handler.Calls is 1.
        result!.Output!.Action.Should().Be("answer");
        handler.Calls.Should().Be(1);
    }

    // TEST-05: gpt-6-luna always sends reasoning_effort=none with the four allowlisted tools; covers null, none and low configured effort.
    /// <summary>Enforces the model's function-calling constraint regardless of deployment configuration.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("none")]
    [InlineData("low")]
    public async Task RunAsync_Gpt6Luna_DisablesReasoningForFunctionCalling(string? configuredEffort)
    {
        // STEP-01: Assert body.RootElement.model is "gpt-6-luna", body.RootElement.reasoning_effort is "none" and body.RootElement.tools.length is 5.
        var handler = new Handler(async request =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            body.RootElement.GetProperty("model").GetString().Should().Be("gpt-6-luna");
            body.RootElement.GetProperty("reasoning_effort").GetString().Should().Be("none");
            body.RootElement.GetProperty("tools").GetArrayLength().Should().Be(6);
            return Response(new { choices = new[] { new { finish_reason = "stop", message = new { content = Output() } } } });
        });
        // STEP-02: Get result from Service().RunAsync.
        var result = await Service(handler, limits: new() { ReasoningEffort = configuredEffort }, model: "gpt-6-luna")
            .RunAsync(AgentTestData.Context(), false, CancellationToken.None);
        // STEP-03: Assert result.Output.Action is "answer" and handler.Calls is 1.
        result!.Output!.Action.Should().Be("answer");
        handler.Calls.Should().Be(1);
    }

    // TEST-06: A tool call whose completion was truncated is discarded as Truncated without executing its arguments or retrying.
    /// <summary>Never executes an apparently complete tool call when the provider reports an incomplete completion.</summary>
    [Fact]
    public async Task RunAsync_TruncatedToolCall_DiscardsArgumentsWithoutRetry()
    {
        // STEP-01: Create the Handler under test as handler.
        var handler = new Handler(_ => Task.FromResult(Response(new
        {
            choices = new[] { new { finish_reason = "length", message = new { content = (string?)null,
                tool_calls = new[] { new { id = "call-cut", type = "function", function = new { name = "get_roadmap", arguments = "{\"roadmapId\":88}" } } } } } },
            usage = new { total_tokens = 17000 }
        })));
        // STEP-02: Get result from Service().RunAsync.
        var result = await Service(handler).RunAsync(AgentTestData.Context(), false, CancellationToken.None);
        // STEP-03: Assert result.Failure.Kind is AgentLlmFailureKind.Truncated, result.Tokens is 17000, result.Tools is empty, result.Output is null and 1 more.
        result!.Failure!.Kind.Should().Be(AgentLlmFailureKind.Truncated);
        result.Tokens.Should().Be(17000);
        result.Tools.Should().BeEmpty(); result.Output.Should().BeNull();
        handler.Calls.Should().Be(1);
    }

    // TEST-07: Reject malformed JSON, missing fields, extra fields, invalid enums, refusal and truncated responses.
    /// <summary>Validates every provider-output boundary before Application receives an actionable step.</summary>
    [Theory]
    [InlineData("json", AgentLlmFailureKind.InvalidSchema)][InlineData("missing", AgentLlmFailureKind.InvalidSchema)]
    [InlineData("extra", AgentLlmFailureKind.InvalidSchema)][InlineData("enum", AgentLlmFailureKind.InvalidSchema)][InlineData("duplicate", AgentLlmFailureKind.InvalidSchema)]
    [InlineData("refusal", AgentLlmFailureKind.Refused)][InlineData("truncated", AgentLlmFailureKind.Truncated)]
    [InlineData("http", AgentLlmFailureKind.ProviderUnavailable)][InlineData("choices", AgentLlmFailureKind.InvalidResponse)]
    public async Task RunAsync_InvalidProviderResponse_ReturnsControlledFailure(string area, AgentLlmFailureKind kind)
    {
        // STEP-01: Derive output from the expected value.
        var output = area switch { "json" => "{", "missing" => "{}", "extra" => Output().TrimEnd('}') + ",\"userId\":99}",
            "enum" => Output().Replace("\"answer\"", "\"delete_roadmap\""), "duplicate" => Output().TrimEnd('}') + ",\"action\":\"answer\"}", _ => Output() };
        // STEP-02: Create the Handler under test as handler.
        var handler = new Handler(_ => Task.FromResult(area == "http" ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) :
            area == "choices" ? Response(new { choices = Array.Empty<object>() }) : Response(new
            {
                choices = new[] { new { finish_reason = area == "truncated" ? "length" : "stop", message = new { content = output, refusal = area == "refusal" ? "refused" : null } } }
            })));
        // STEP-03: Get result from Service().RunAsync.
        var result = await Service(handler).RunAsync(AgentTestData.Context(), false, CancellationToken.None);
        // STEP-04: Assert result.Failure.Kind is kind, result.Output is null, result.Tools is empty and handler.Calls is 1.
        result!.Failure!.Kind.Should().Be(kind); result.Output.Should().BeNull(); result.Tools.Should().BeEmpty();
        handler.Calls.Should().Be(1);
    }

    // TEST-08: Invalid output reports the first mismatching schema path and masks invented field names; covers malformed JSON, missing, unexpected, invented, enum and nested-enum fields.
    /// <summary>Names the first mismatching schema path so a repair is targeted, masking invented field names.</summary>
    [Theory]
    [InlineData("json", "output_json")]
    [InlineData("missing", "output:$.action(missing)")]
    [InlineData("extra", "output:$.userId(unexpected)")]
    [InlineData("invented", "output:$.?(unexpected)")]
    [InlineData("enum", "output:$.action")]
    [InlineData("nested", "output:$.scope.mealSlot")]
    public async Task RunAsync_InvalidSchema_ReportsSafeSchemaPath(string area, string reason)
    {
        // STEP-01: Derive output from the expected value.
        var output = area switch { "json" => "{", "missing" => "{}", "extra" => Output().TrimEnd('}') + ",\"userId\":99}",
            "invented" => Output().TrimEnd('}') + ",\"private prompt text\":1}", "enum" => Output().Replace("\"answer\"", "\"delete_roadmap\""),
            _ => Output().Replace("\"mealSlot\":null", "\"mealSlot\":\"brunch\"") };
        // STEP-02: Create the Handler under test as handler.
        var handler = new Handler(_ => Task.FromResult(Response(new
            { choices = new[] { new { finish_reason = "stop", message = new { content = output, refusal = (string?)null } } } })));

        // STEP-03: Get result from Service().RunAsync.
        var result = await Service(handler).RunAsync(AgentTestData.Context(), false, CancellationToken.None);

        // STEP-04: Assert result.Failure.Kind is AgentLlmFailureKind.InvalidSchema and result.Failure.Reason is reason.
        result!.Failure!.Kind.Should().Be(AgentLlmFailureKind.InvalidSchema);
        result.Failure.Reason.Should().Be(reason);
    }

    // TEST-09: Invalid tool calls report a fixed reason without model text; covers too many calls, a non-function type, an unknown name,
    // malformed argument JSON, an argument schema path and tool calls returned with finish_reason=stop.
    /// <summary>Makes tool-call schema failures diagnosable while keeping unknown tool names out of logs.</summary>
    [Theory]
    [InlineData("count", "tool_call_count")]
    [InlineData("type", "tool_type")]
    [InlineData("name", "tool_name")]
    [InlineData("json", "tool_json:get_roadmap")]
    [InlineData("args", "tool_args:get_roadmap:$.roadmapId")]
    [InlineData("finish", "tool_calls_with_finish:stop")]
    public async Task RunAsync_InvalidToolCall_ReportsSafeReason(string area, string reason)
    {
        // STEP-01: Get calls from Range().Select(filtered).ToArray.
        var calls = Enumerable.Range(0, area == "count" ? 7 : 1).Select(index => ToolCall($"call-{index}", area)).ToArray();
        // STEP-02: Create the Handler under test as handler.
        var handler = new Handler(_ => Task.FromResult(Response(new { choices = new[] { new
            { finish_reason = area == "finish" ? "stop" : "tool_calls", message = new { tool_calls = calls } } } })));

        // STEP-03: Get result from Service().RunAsync.
        var result = await Service(handler).RunAsync(AgentTestData.Context(), false, CancellationToken.None);

        // STEP-04: Assert result.Failure.Kind is AgentLlmFailureKind.InvalidSchema, result.Failure.Reason is reason and result.Tools is empty.
        result!.Failure!.Kind.Should().Be(AgentLlmFailureKind.InvalidSchema);
        result.Failure.Reason.Should().Be(reason).And.NotContain("private");
        result.Tools.Should().BeEmpty();
    }

    // TEST-10: A context with tools closed after a duplicate request sends tool_choice=none before the round budget is spent.
    /// <summary>A repeated identical tool request closes tools for the next call even before the round budget is spent.</summary>
    [Fact]
    public async Task RunAsync_ToolsClosed_ForcesFinalOutput()
    {
        // STEP-01: Assert sent.RootElement.tool_choice is "none".
        var handler = new Handler(async request =>
        {
            using var sent = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            sent.RootElement.GetProperty("tool_choice").GetString().Should().Be("none");
            return Response(new { choices = new[] { new { finish_reason = "stop", message = new { content = Output(), refusal = (string?)null } } } });
        });
        // STEP-02: Get context from AgentTestData.Context, set context.ToolsClosed to true and get result from Service().RunAsync.
        var context = AgentTestData.Context(); context.ToolsClosed = true;

        var result = await Service(handler).RunAsync(context, false, CancellationToken.None);

        // STEP-03: Assert result.Output.Action is "answer" and handler.Calls is 1.
        result!.Output!.Action.Should().Be("answer");
        handler.Calls.Should().Be(1);
    }

    // TEST-11: Preserve provider tool call IDs and parse only allowlisted strictly validated arguments.
    /// <summary>Verifies tool-call/result correlation needed by subsequent provider rounds.</summary>
    [Fact]
    public async Task RunAsync_ToolCall_PreservesProviderIdentity()
    {
        // STEP-01: Create the Handler under test as handler.
        var handler = new Handler(_ => Task.FromResult(Response(new { choices = new[] { new { finish_reason = "tool_calls", message = new
        { tool_calls = new[] { new { id = "call-owned", type = "function", function = new { name = "get_roadmap", arguments = "{\"roadmapId\":88}" } } } } } } })));
        // STEP-02: Get result from Service().RunAsync.
        var result = await Service(handler).RunAsync(AgentTestData.Context(), false, CancellationToken.None);
        // STEP-03: Assert result.Tools.Single().Id is "call-owned" and result.Tools.Single().Name is "get_roadmap".
        result!.Tools.Single().Id.Should().Be("call-owned"); result.Tools.Single().Name.Should().Be("get_roadmap");
    }

    // TEST-12: Missing provider credentials avoid HTTP calls and caller cancellation is never converted to fallback.
    /// <summary>Verifies configuration and cancellation boundaries.</summary>
    [Fact]
    public async Task RunAsync_NoCredentialsOrCancellation_RespectsBoundary()
    {
        // STEP-01: Create the Handler under test as handler.
        var handler = new Handler((_, ct) => Task.FromCanceled<HttpResponseMessage>(ct));
        // STEP-02: Get service from Service.
        var service = Service(handler, "");
        // STEP-03: Assert service.RunAsync().Failure.Kind is AgentLlmFailureKind.Configuration and handler.Calls is 0.
        (await service.RunAsync(AgentTestData.Context(), false, CancellationToken.None))!.Failure!.Kind.Should().Be(AgentLlmFailureKind.Configuration); handler.Calls.Should().Be(0);
        // STEP-04: Create cancelled as CancellationTokenSource and cancel cancelled.
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        // STEP-05: Wrap Service().RunAsync in act so its outcome can be asserted.
        var act = () => Service(handler).RunAsync(AgentTestData.Context(), false, cancelled.Token);
        // STEP-06: Assert act throws OperationCanceledException.
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // TEST-13: Gemini requires its opaque tool signature on the subsequent assistant message.
    /// <summary>Checks a real parse/replay boundary and keeps provider metadata out of stored tool transcripts.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunAsync_ToolRoundTrip_PreservesOptionalProviderMetadata(bool signed)
    {
        // STEP-01: Create call as Dictionary<string, object?> and when signed, set call["extra_content"] to a test value.
        var call = new Dictionary<string, object?> { ["id"] = "call-signed", ["type"] = "function",
            ["function"] = new { name = "get_roadmap", arguments = "{\"roadmapId\":88}" } };
        if (signed) call["extra_content"] = new { google = new { thought_signature = "opaque-provider-signature" } };
        // STEP-02: Assert replay.id is "call-signed", replay.TryGetProperty() is signed, metadata.google.thought_signature is "opaque-provider-signature" and 1 more.
        var handler = new Handler(async request =>
        {
            if (request.Content is not null)
            {
                using var body = JsonDocument.Parse(await request.Content.ReadAsStringAsync());
                var messages = body.RootElement.GetProperty("messages");
                if (messages.GetArrayLength() > 2)
                {
                    var replay = messages[2].GetProperty("tool_calls")[0];
                    replay.GetProperty("id").GetString().Should().Be("call-signed");
                    replay.TryGetProperty("extra_content", out var metadata).Should().Be(signed);
                    if (signed) metadata.GetProperty("google").GetProperty("thought_signature").GetString().Should().Be("opaque-provider-signature");
                    messages[3].GetProperty("tool_call_id").GetString().Should().Be("call-signed");
                    return Response(new { choices = new[] { new { finish_reason = "stop", message = new { content = Output() } } } });
                }
            }
            return Response(new { choices = new[] { new { finish_reason = "tool_calls", message = new { tool_calls = new[] { call } } } } });
        });
        // STEP-03: Get service from Service, get context from AgentTestData.Context and get first from service.RunAsync.
        var service = Service(handler);
        var context = AgentTestData.Context();
        var first = await service.RunAsync(context, false, CancellationToken.None);
        // STEP-04: Assert first is not null, Tools.Single().ProviderExtraContent.HasValue is signed and AgentJson.Write() does not contain "opaque-provider-signature".
        first.Should().NotBeNull();
        first!.Tools.Single().ProviderExtraContent.HasValue.Should().Be(signed);
        AgentJson.Write(first.Tools).Should().NotContain("opaque-provider-signature").And.NotContain("providerExtraContent");
        // STEP-05: Add the computed value to context.Exchanges and get second from service.RunAsync.
        context.Exchanges.Add(new(first.Tools, [JsonSerializer.SerializeToElement(new { roadmapId = 88 })]));
        var second = await service.RunAsync(context, false, CancellationToken.None);
        // STEP-06: Assert second.Output.Action is "answer" and handler.Calls is 2.
        second!.Output!.Action.Should().Be("answer");
        handler.Calls.Should().Be(2);
    }

    // TEST-14: Escaped Unicode in nested JSON can make a provider return literal escape sequences as province names.
    /// <summary>Preserves readable Vietnamese inside both context and tool data messages without changing persisted JSON.</summary>
    [Fact]
    public async Task RunAsync_VietnameseContextAndToolResult_RemainReadable()
    {
        // STEP-01: Assert messages[1].content contains the expected text and messages[3].content contains the expected text.
        var handler = new Handler(async request =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var messages = body.RootElement.GetProperty("messages");
            messages[1].GetProperty("content").GetString().Should().Contain("Hà Nội");
            messages[3].GetProperty("content").GetString().Should().Contain("Phở bò Hà Nội");
            return Response(new { choices = new[] { new { finish_reason = "stop", message = new { content = Output() } } } });
        });
        // STEP-02: Get context from AgentTestData.Context, set context.State to a copy of context.State and add the computed value to context.Exchanges.
        var context = AgentTestData.Context();
        context.State = context.State with { Province = "Hà Nội" };
        context.Exchanges.Add(new([new("call-vi", "get_roadmap", JsonSerializer.SerializeToElement(new { roadmapId = 88 }))],
            [JsonSerializer.SerializeToElement(new { dishName = "Phở bò Hà Nội" }, AgentJson.Options)]));
        // STEP-03: Assert Service().RunAsync().Output.Action is "answer".
        (await Service(handler).RunAsync(context, false, CancellationToken.None))!.Output!.Action.Should().Be("answer");
    }

    // TEST-15: Preserve safe HTTP status and Retry-After without reading or exposing a provider error body.
    /// <summary>Distinguishes quota, authentication, rejected requests and unavailable services with one transport attempt.</summary>
    [Theory]
    [InlineData(429, AgentLlmFailureKind.RateLimited)]
    [InlineData(401, AgentLlmFailureKind.Authentication)][InlineData(403, AgentLlmFailureKind.Authentication)]
    [InlineData(400, AgentLlmFailureKind.RejectedRequest)]
    [InlineData(500, AgentLlmFailureKind.ProviderUnavailable)][InlineData(503, AgentLlmFailureKind.ProviderUnavailable)]
    [InlineData(408, AgentLlmFailureKind.Timeout)][InlineData(504, AgentLlmFailureKind.Timeout)]
    public async Task RunAsync_HttpFailure_PreservesOnlySafeMetadata(int status, AgentLlmFailureKind kind)
    {
        // STEP-01: Create the Handler under test as handler.
        var handler = new Handler(_ =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("private-key-and-prompt-must-not-leak") };
            response.Headers.RetryAfter = new(TimeSpan.FromSeconds(45)); return Task.FromResult(response);
        });
        // STEP-02: Get result from Service().RunAsync.
        var result = await Service(handler).RunAsync(AgentTestData.Context(), false, CancellationToken.None);
        // STEP-03: Assert result.Failure is a new AgentLlmFailure, AgentJson.Write() does not contain "private-key" and handler.Calls is 1.
        result!.Failure.Should().Be(new AgentLlmFailure(kind, status, 45));
        AgentJson.Write(result).Should().NotContain("private-key").And.NotContain("test-key"); handler.Calls.Should().Be(1);
    }

    // TEST-16: A high-demand 503 keeps only the provider code and the high_demand reason, never the raw message.
    /// <summary>Classifies provider overload safely without leaking upstream error text.</summary>
    [Fact]
    public async Task RunAsync_GeminiHighDemand_PreservesSafeReasonWithoutRawMessage()
    {
        // STEP-01: Create the Handler under test as handler.
        var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        { Content = new StringContent("{\"error\":{\"status\":\"UNAVAILABLE\",\"message\":\"This model is experiencing high demand. private-key-must-not-leak\"}}") }));
        // STEP-02: Get result from Service().RunAsync.
        var result = await Service(handler).RunAsync(AgentTestData.Context(), false, CancellationToken.None);
        // STEP-03: Assert result.Failure.ProviderCode is "UNAVAILABLE", result.Failure.Reason is "high_demand" and AgentJson.Write() does not contain "private-key".
        result!.Failure!.ProviderCode.Should().Be("UNAVAILABLE"); result.Failure.Reason.Should().Be("high_demand");
        AgentJson.Write(result).Should().NotContain("private-key");
    }

    // TEST-17: OpenAI hard spend limits map to a non-retryable configuration failure with a quota message; covers the project and the organization limit.
    /// <summary>Keeps spending-limit failures distinct from rate limits and provider outages so they are never retried.</summary>
    [Theory]
    [InlineData("project_spend_limit_exceeded")]
    [InlineData("organization_spend_limit_exceeded")]
    public async Task RunAsync_SpendLimitFailure_IsNotARetryableProviderOutage(string code)
    {
        // STEP-01: Create the Handler under test as handler.
        var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests) {
            Content = new StringContent(JsonSerializer.Serialize(new { error = new { code, message = "private diagnostic" } })) }));
        // STEP-02: Get result from Service().RunAsync.
        var result = await Service(handler).RunAsync(AgentTestData.Context(), false, CancellationToken.None);
        // STEP-03: Assert result.Failure.Kind is AgentLlmFailureKind.Configuration and result.Failure.Reason is "budget_control".
        result!.Failure!.Kind.Should().Be(AgentLlmFailureKind.Configuration);
        result.Failure.Reason.Should().Be("budget_control");
        // STEP-04: Create userError as ANGI.Application.Common.Exceptions.AgentProviderException.
        var userError = new ANGI.Application.Common.Exceptions.AgentProviderException(result.Failure);
        // STEP-05: Assert userError.Message contains the expected text, AgentJson.Write() does not contain "private diagnostic" and handler.Calls is 1.
        userError.Message.Should().Contain("hạn mức");
        AgentJson.Write(result).Should().NotContain("private diagnostic");
        handler.Calls.Should().Be(1);
    }

    // TEST-18: Unusual 429 bodies still classify as RateLimited with status 429; covers empty object array, empty array, null and a bare string.
    /// <summary>Ensures malformed error envelopes cannot hide a rate-limit response.</summary>
    [Theory]
    [InlineData("[{}]")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"private-message\"")]
    public async Task RunAsync_UnusualErrorEnvelope_PreservesHttp429(string body)
    {
        // STEP-01: Create the Handler under test as handler.
        var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent(body) }));
        // STEP-02: Get result from Service().RunAsync.
        var result = await Service(handler).RunAsync(AgentTestData.Context(), false, CancellationToken.None);
        // STEP-03: Assert result.Failure.Kind is AgentLlmFailureKind.RateLimited and result.Failure.HttpStatus is 429.
        result!.Failure!.Kind.Should().Be(AgentLlmFailureKind.RateLimited);
        result.Failure.HttpStatus.Should().Be(429);
    }

    // TEST-19: A Gemini array-wrapped 429 yields RESOURCE_EXHAUSTED, the rate_limit reason and a rounded-up retry delay.
    /// <summary>Reads Gemini's array error envelope so short limits can be retried at the right time.</summary>
    [Fact]
    public async Task RunAsync_GeminiArrayRateLimit_ReadsStatusAndRetryInfo()
    {
        // STEP-01: Define body as the expected text.
        var body = "[{\"error\":{\"code\":429,\"status\":\"RESOURCE_EXHAUSTED\",\"message\":\"Quota exceeded: GenerateRequestsPerMinutePerProject\",\"details\":[{\"retryDelay\":\"12.5s\"}]}}]";
        // STEP-02: Create the Handler under test as handler.
        var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent(body) }));
        // STEP-03: Get result from Service().RunAsync.
        var result = await Service(handler).RunAsync(AgentTestData.Context(), false, CancellationToken.None);
        // STEP-04: Assert result.Failure is a new AgentLlmFailure.
        result!.Failure.Should().Be(new AgentLlmFailure(AgentLlmFailureKind.RateLimited, 429, 13, "RESOURCE_EXHAUSTED", "rate_limit"));
    }

    // TEST-20: HttpClient timeout and a transport exception remain distinct from received malformed output.
    /// <summary>Exercises cancellation-aware fake transport without making any network request.</summary>
    [Theory]
    [InlineData(true)][InlineData(false)]
    public async Task RunAsync_TransportFailure_IsNotSchemaFailure(bool timeout)
    {
        // STEP-01: Create the Handler under test as handler.
        var handler = new Handler(async (_, ct) =>
        {
            if (timeout) await Task.Delay(Timeout.Infinite, ct);
            throw new HttpRequestException("sensitive internal endpoint");
        });
        // STEP-02: Create http as HttpClient.
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://provider.test/v1/"), Timeout = TimeSpan.FromMilliseconds(100) };
        // STEP-03: Create the OpenAiAgentLlmService under test as service.
        var service = new OpenAiAgentLlmService(http, Options.Create(new AiSettings { ApiKey = "test-key", Model = "test-model" }), new(), TimeProvider.System);
        // STEP-04: Run service.RunAsync and keep result.
        var result = await service.RunAsync(AgentTestData.Context(), false, CancellationToken.None);
        // STEP-05: Assert result.Failure.Kind is the expected value, AgentJson.Write() does not contain "sensitive" and handler.Calls is 1.
        result!.Failure!.Kind.Should().Be(timeout ? AgentLlmFailureKind.Timeout : AgentLlmFailureKind.Network);
        AgentJson.Write(result).Should().NotContain("sensitive"); handler.Calls.Should().Be(1);
    }

    // TEST-21: Date-based retry hints use the injected clock and malformed or expired hints are ignored.
    /// <summary>Checks deterministic retry guidance without weakening HTTP failure classification.</summary>
    [Theory]
    [InlineData("future", 30)][InlineData("past", null)][InlineData("invalid", null)]
    public async Task RunAsync_RetryAfter_UsesSafeClockBasedDelay(string hint, int? expected)
    {
        // STEP-01: Create clock as FixedClock.
        var clock = new FixedClock();
        // STEP-02: Create the Handler under test as handler.
        var handler = new Handler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            if (hint == "invalid") response.Headers.TryAddWithoutValidation("Retry-After", "not-a-date");
            else response.Headers.RetryAfter = new(clock.GetUtcNow().AddSeconds(hint == "future" ? 30 : -30));
            return Task.FromResult(response);
        });
        // STEP-03: Create http as HttpClient.
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://provider.test/v1/") };
        // STEP-04: Create the OpenAiAgentLlmService under test as service.
        var service = new OpenAiAgentLlmService(http, Options.Create(new AiSettings { ApiKey = "test-key", Model = "test-model" }), new(), clock);
        // STEP-05: Run service.RunAsync and keep result.
        var result = await service.RunAsync(AgentTestData.Context(), false, CancellationToken.None);
        // STEP-06: Assert result.Failure.Kind is AgentLlmFailureKind.RateLimited and result.Failure.RetryAfterSeconds is expected.
        result!.Failure!.Kind.Should().Be(AgentLlmFailureKind.RateLimited); result.Failure.RetryAfterSeconds.Should().Be(expected);
    }

    // TEST-22: An invalid provider envelope retains HTTP 200 but is not misclassified as received model content needing repair.
    /// <summary>Distinguishes protocol corruption from malformed JSON produced inside a valid model response.</summary>
    [Fact]
    public async Task RunAsync_InvalidEnvelope_PreservesStatusWithoutSchemaRepair()
    {
        // STEP-01: Create the Handler under test as handler.
        var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not-valid-provider-json") }));
        // STEP-02: Get result from Service().RunAsync.
        var result = await Service(handler).RunAsync(AgentTestData.Context(), false, CancellationToken.None);
        // STEP-03: Assert result.Failure is a new AgentLlmFailure and handler.Calls is 1.
        result!.Failure.Should().Be(new AgentLlmFailure(AgentLlmFailureKind.InvalidResponse, 200)); handler.Calls.Should().Be(1);
    }

    /// <summary>Supplies an exact clock for HTTP-date retry guidance.</summary>
    private sealed class FixedClock : TimeProvider
    {
        /// <summary>Returns the deterministic instant used to interpret provider HTTP dates.</summary>
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 28, 0, 0, 0, TimeSpan.Zero);
    }

    // TEST-23: An OpenAI per-minute rate limit stays a temporary rate limit that keeps its Retry-After, never a spending-limit
    // failure; covers rate_limit_exceeded with two retry hints.
    /// <summary>Lets the orchestrator wait and retry a per-minute limit instead of telling the user to contact an administrator.</summary>
    [Theory]
    [InlineData("rate_limit_exceeded", 7)]
    [InlineData("rate_limit_exceeded", 3)]
    public async Task RunAsync_ProviderTemporaryLimit_IsRetryableRateLimit(string code, int retryAfter)
    {
        // STEP-01: Create the Handler under test as handler.
        var handler = new Handler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) {
                Content = new StringContent(JsonSerializer.Serialize(new { error = new { message = "Rate limit reached for requests.", type = "requests", code } })) };
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(retryAfter));
            return Task.FromResult(response);
        });

        // STEP-02: Get result from Service().RunAsync.
        var result = await Service(handler).RunAsync(AgentTestData.Context(), false, CancellationToken.None);

        // STEP-03: Assert result.Failure is a new AgentLlmFailure and Exceptions.AgentProviderException().Message does not contain the expected text.
        result!.Failure.Should().Be(new AgentLlmFailure(AgentLlmFailureKind.RateLimited, 429, retryAfter, code, "rate_limit"));
        new ANGI.Application.Common.Exceptions.AgentProviderException(result.Failure!).Message.Should().NotContain("quản trị viên");
    }

    // TEST-24: Every request tells the model its scope and offers the chat and out_of_scope actions with suggestedDish in the strict schema.
    /// <summary>Without these the model has no way to decline "hướng dẫn bóc cam" and falls back to answer or clarify.</summary>
    [Fact]
    public async Task RunAsync_Request_DeclaresScopeAndSmallTalkActions()
    {
        // STEP-01: Assert system contains the expected text, action.enum.EnumerateArray().Select(filtered) contains the expected items and 2 more.
        var handler = new Handler(async request =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var system = body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString();
            system.Should().Contain("action=chat is only for social messages").And.Contain("action=out_of_scope is for everything else")
                .And.Contain("hướng dẫn bóc cam").And.Contain("replaces it with fixed text").And.Contain("the backend asks for them when the user saves");
            var schema = body.RootElement.GetProperty("response_format").GetProperty("json_schema").GetProperty("schema").GetProperty("properties");
            schema.GetProperty("action").GetProperty("enum").EnumerateArray().Select(value => value.GetString()).Should().Contain(["chat", "out_of_scope"]);
            schema.TryGetProperty("suggestedDish", out _).Should().BeTrue();
            return Response(new { choices = new[] { new { finish_reason = "stop", message = new { content = Output() } } } });
        });

        (await Service(handler).RunAsync(AgentTestData.Context(), false, CancellationToken.None))!.Output.Should().NotBeNull();
    }

    // TEST-25: The model is told whether a plan exists and where ANGI has data, with the steps of a new trip and the rules from conversation 158.
    /// <summary>These rules replaced the backend's reading of the questions, so losing one changes the flow.</summary>
    [Fact]
    public async Task RunAsync_NewTrip_IsSentWithItsRules()
    {
        // STEP-01: Assert the request data and messages[0].content.
        var handler = new Handler(async request =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var messages = body.RootElement.GetProperty("messages");
            messages[0].GetProperty("content").GetString().Should().Contain("Take these steps in this order").And.Contain("only when the user gave nothing but a province").And.Contain("clarification=place_scope").And.Contain("clarification=preferences")
                .And.Contain("budgetUnrestricted=true and budgetAmount=null").And.Contain("Call locate_places once").And.Contain("Dùng vị trí hiện tại").And.Contain("catalogProvinces")
                .And.Contain("gợi ý code giúp tôi trang giao diện").And.Contain("tôi muốn ăn ngọt").And.Contain("nhà tôi ở Cầu Giấy").And.Contain("Mention a plan only when hasPlan=true");
            using var data = JsonDocument.Parse(messages[1].GetProperty("content").GetString()!);
            data.RootElement.GetProperty("hasPlan").GetBoolean().Should().BeFalse();
            data.RootElement.GetProperty("catalogProvinces")[0].GetString().Should().Be("Hà Nội");
            data.RootElement.TryGetProperty("intakeAside", out _).Should().BeFalse();
            body.RootElement.GetProperty("tools").EnumerateArray().Select(tool => tool.GetProperty("function").GetProperty("name").GetString()).Should().Contain("locate_places");
            return Response(new { choices = new[] { new { finish_reason = "stop", message = new { content = Output() } } } });
        });
        // STEP-02: Get context from AgentTestData.Context and set the catalog's provinces.
        var context = AgentTestData.Context(); context.CatalogProvinces = ["Hà Nội"];

        // STEP-03: Assert Service().RunAsync().Output is not null.
        (await Service(handler).RunAsync(context, false, CancellationToken.None))!.Output.Should().NotBeNull();
    }

    // TEST-26: The model is told to word a suggestion from the tool's facts, to claim taste only when ranked by it, and to admit it does not know the user's own favourites.
    /// <summary>These rules keep the model's natural wording inside what the backend's checks accept.</summary>
    [Fact]
    public async Task RunAsync_Request_DeclaresSuggestionAndFavouriteRules()
    {
        // STEP-01: Assert messages[0].content contains each rule.
        var handler = new Handler(async request =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            using var data = JsonDocument.Parse(body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
            data.RootElement.GetProperty("dishOffered").GetBoolean().Should().BeTrue();
            body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString().Should().Contain("món sở trường của tôi là gì")
                .And.Contain("never out_of_scope").And.Contain("only when personalized=true").And.Contain("status=nothing_found")
                .And.Contain("{name1}, {restaurant1}, {price1} and {address1}").And.Contain("means 0.75 for this call only").And.Contain("Outside suggest_dish, a bare preference")
                .And.Contain("Set count to how many the user's latest message asks for").And.Contain("copy into levelQuote the exact words the user wrote").And.Contain("{total} always").And.Contain("{changes} when editing").And.Contain("how you read it and invite a change").And.Contain("'tôi thích ngọt'").And.Contain("When hasPlan=true").And.Contain("dishOffered=true").And.Contain("beside that dish or replace it")
                .And.Contain("gợi ý món theo sở thích của tôi").And.NotContain("The backend answers the user itself");
            return Response(new { choices = new[] { new { finish_reason = "stop", message = new { content = Output() } } } });
        });

        // STEP-02: Get context from AgentTestData.Context and set context.DishOffered to true; assert the request tells the model.
        var context = AgentTestData.Context(); context.DishOffered = true;
        (await Service(handler).RunAsync(context, false, CancellationToken.None))!.Output.Should().NotBeNull();
    }

    // TEST-27: The wording call closes tools and asks for the reply, never for a proposal; covers a suggestion found and the ordinary exhausted-tools message.
    /// <summary>The "return the final proposal" message would push the model away from wording the suggestion.</summary>
    [Theory]
    [InlineData(true, "The suggestion above is ready")]
    [InlineData(false, "The tool-round budget is exhausted")]
    public async Task RunAsync_ToolsClosed_AsksForTheRightOutput(bool suggested, string expected)
    {
        // STEP-01: Get context from AgentTestData.Context, close tools and set context.DishSuggestion when suggested.
        var context = AgentTestData.Context();
        context.ToolsClosed = true;
        if (suggested) context.DishSuggestion = new([new("Bún Chả", "Quán A", 45000, null)], false, false, "Mình gợi ý Bún Chả.");
        // STEP-02: Assert the last message contains expected and tool_choice is "none".
        var handler = new Handler(async request =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var messages = body.RootElement.GetProperty("messages");
            messages[messages.GetArrayLength() - 1].GetProperty("content").GetString().Should().Contain(expected);
            body.RootElement.GetProperty("tool_choice").GetString().Should().Be("none");
            return Response(new { choices = new[] { new { finish_reason = "stop", message = new { content = Output() } } } });
        });

        // STEP-03: Assert Service().RunAsync().Output is not null.
        (await Service(handler).RunAsync(context, false, CancellationToken.None))!.Output.Should().NotBeNull();
    }

    // TEST-28: The date given to the model is the Vietnam date the backend uses; covers just before and at 17:00 UTC, when the day changes in Vietnam.
    /// <summary>Stops "ngày mai" from meaning a different day to the model and to the backend's save check between midnight and 07:00.</summary>
    [Theory]
    [InlineData(16, 59, "2026-11-04")][InlineData(17, 0, "2026-11-05")]
    public async Task RunAsync_CurrentDate_UsesVietnamDay(int hour, int minute, string expected)
    {
        // STEP-01: Assert data.RootElement.currentDate is expected.
        var handler = new Handler(async request =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            using var data = JsonDocument.Parse(body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
            data.RootElement.GetProperty("currentDate").GetString().Should().Be(expected);
            return Response(new { choices = new[] { new { finish_reason = "stop", message = new { content = Output() } } } });
        });
        // STEP-02: Create the OpenAiAgentLlmService under test as service.
        var service = new OpenAiAgentLlmService(new HttpClient(handler) { BaseAddress = new Uri("https://provider.test/v1/") },
            Options.Create(new AiSettings { ApiKey = "test-key", Model = "test-model" }), new(), new ClockAt(new(2026, 11, 4, hour, minute, 0, TimeSpan.Zero)));

        // STEP-03: Assert service.RunAsync().Output is not null.
        (await service.RunAsync(AgentTestData.Context(), false, CancellationToken.None))!.Output.Should().NotBeNull();
    }

    /// <summary>Supplies a chosen instant so a day boundary can be tested exactly.</summary>
    private sealed class ClockAt(DateTimeOffset now) : TimeProvider
    {
        /// <summary>Returns the chosen instant.</summary>
        public override DateTimeOffset GetUtcNow() => now;
    }

    // TEST-40: The read step offers no tools, sends real chat turns with the latest message last, and only the rules of its stage; covers a new trip and a waiting answer.
    /// <summary>The model reads the message without holding a tool it could reach for.</summary>
    [Theory]
    [InlineData(null, "# 4. A new trip", "# 8. Building the proposal")]
    [InlineData(AgentConfirmationPolicy.AwaitingSave, "# 10. Constraints", "# 4. A new trip")]
    public async Task RunAsync_ReadStep_HasNoToolsAndOnlyItsRules(string? awaited, string kept, string dropped)
    {
        // STEP-01: Get context in the read phase with one earlier exchange.
        var context = AgentTestData.Context();
        context.Phase = AgentTurnPhase.Read;
        context.AwaitedAnswer = awaited;
        context.History.Add(new("user", "chốt đi"));
        context.History.Add(new("assistant", "Mình lưu nhé?"));
        context.Request.Content = "đừng";
        var handler = new Handler(async request =>
        {
            // STEP-02: Assert the request has no tools and the messages end with the real turns.
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var root = body.RootElement;
            root.TryGetProperty("tools", out _).Should().BeFalse();
            root.TryGetProperty("tool_choice", out _).Should().BeFalse();
            var messages = root.GetProperty("messages").EnumerateArray().ToArray();
            messages[0].GetProperty("content").GetString().Should().Contain("You have no tools in this step").And.Contain(kept).And.NotContain(dropped);
            messages[^3].GetProperty("role").GetString().Should().Be("user");
            messages[^2].GetProperty("role").GetString().Should().Be("assistant");
            messages[^1].GetProperty("role").GetString().Should().Be("user");
            messages[^1].GetProperty("content").GetString().Should().Be("đừng");
            messages[1].GetProperty("role").GetString().Should().Be("system");
            using var data = JsonDocument.Parse(messages[1].GetProperty("content").GetString()!);
            data.RootElement.GetProperty("history").ValueKind.Should().Be(JsonValueKind.Null);
            data.RootElement.GetProperty("request").ValueKind.Should().Be(JsonValueKind.Null);
            return Response(new { choices = new[] { new { finish_reason = "stop", message = new { content = Output() } } } });
        });

        // STEP-03: Assert the output is read.
        (await Service(handler).RunAsync(context, false, CancellationToken.None))!.Output.Should().NotBeNull();
    }

    // TEST-41: The forced step offers only the named tool and must call it.
    /// <summary>After reading, the model cannot pick a different tool.</summary>
    [Fact]
    public async Task RunAsync_ForcedStep_OffersOnlyTheNamedTool()
    {
        // STEP-01: Get context in the forced phase.
        var context = AgentTestData.Context();
        context.Phase = AgentTurnPhase.Forced;
        context.ForcedTool = "suggest_dish";
        var handler = new Handler(async request =>
        {
            // STEP-02: Assert the single tool and the forced tool choice.
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var root = body.RootElement;
            root.GetProperty("tools").EnumerateArray().Select(tool => tool.GetProperty("function").GetProperty("name").GetString()).Should().Equal("suggest_dish");
            root.GetProperty("tool_choice").GetProperty("function").GetProperty("name").GetString().Should().Be("suggest_dish");
            return Response(new { choices = new[] { new { finish_reason = "stop", message = new { content = Output() } } } });
        });

        // STEP-03: Assert the output is read.
        (await Service(handler).RunAsync(context, false, CancellationToken.None))!.Output.Should().NotBeNull();
    }

    /// <summary>Creates the strict response object while keeping every nullable field explicit.</summary>
    private static string Output() => JsonSerializer.Serialize(new { action = "answer", reply = "Explanation", state = (object?)null,
        suggestedDish = (string?)null, clarification = (string?)null, tool = (string?)null, pendingAnswer = (string?)null, answerStartDate = (string?)null, answerEndDate = (string?)null,
        constraintsChangeConfirmed = false, baseProposalVersion = (int?)null, scope = new { dayNumber = (short?)null, mealSlot = (string?)null }, items = Array.Empty<object>(), changes = Array.Empty<object>(), dishNotes = Array.Empty<object>(), daySummaries = Array.Empty<object>() });
    /// <summary>Creates a typed provider client configured with deterministic non-secret credentials.</summary>
    private static OpenAiAgentLlmService Service(HttpMessageHandler handler, string key = "test-key", AgentLimits? limits = null, string model = "configured-model") => new(
        new HttpClient(handler) { BaseAddress = new Uri("https://provider.test/v1/") }, Options.Create(new AiSettings { ApiKey = key, Model = model }), limits ?? new AgentLimits(), TimeProvider.System);
    /// <summary>Builds one provider tool call whose type, name or arguments are corrupted according to the scenario.</summary>
    private static object ToolCall(string id, string area) => new { id, type = area == "type" ? "custom" : "function", function = new
    {
        name = area == "name" ? "delete_roadmap private" : "get_roadmap",
        arguments = area switch { "json" => "{", "args" => "{\"roadmapId\":\"88\"}", _ => "{\"roadmapId\":88}" }
    } };
    /// <summary>Encodes an OpenAI-compatible test response as JSON.</summary>
    private static HttpResponseMessage Response(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };

    /// <summary>Provides deterministic HTTP responses without a live provider call.</summary>
    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _send;
        public int Calls;
        /// <summary>Adapts a request-only responder.</summary>
        public Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) => _send = (request, _) => send(request);
        /// <summary>Adapts a cancellation-aware responder.</summary>
        public Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => _send = send;
        /// <summary>Returns the scripted response while recording transport use.</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Interlocked.Increment(ref Calls); return _send(request, cancellationToken); }
    }
}
