using System.Net;
using System.Text;
using System.Text.Json;
using ANGI.Application.Common.Models.Agent;
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
        var context = AgentTestData.Context(AgentTestData.Snapshot(AgentTestData.Item()));
        context.ProposalVersion = 3;
        context.Grants.Add(new(1, "breakfast", [221], Guid.NewGuid(), context.State));
        context.RejectedOutput = new AgentOutput { Action = "create_proposal", Reply = "Draft", Items = [AgentTestData.Item(999)] };
        context.RepairFeedback = "Món 999 cần nguồn ứng viên mới đúng phạm vi và khẩu vị.";
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
        var handler = new Handler(async request =>
        {
            using var sent = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            sent.RootElement.GetProperty("tool_choice").GetString().Should().Be("none");
            return Response(new { choices = new[] { new { finish_reason = "stop", message = new { content = Output() } } } });
        });
        var context = AgentTestData.Context();
        for (var round = 0; round < 3; round++) context.Exchanges.Add(new([], []));
        (await Service(handler).RunAsync(context, false, CancellationToken.None))!.Output!.Action.Should().Be("answer");
    }

    // TEST-03: Send allowlisted strict tool schemas and parse a complete structured answer with token usage.
    /// <summary>Verifies the exact HTTP authentication and structured-output contract without a live API key.</summary>
    [Fact]
    public async Task RunAsync_StrictAnswer_UsesExpectedProviderContract()
    {
        string? body = null;
        var output = Output();
        var handler = new Handler(async request =>
        {
            request.Headers.Authorization!.Scheme.Should().Be("Bearer"); request.Headers.Authorization.Parameter.Should().Be("test-key");
            request.RequestUri!.AbsolutePath.Should().Be("/v1/chat/completions");
            body = await request.Content!.ReadAsStringAsync();
            return Response(new { choices = new[] { new { finish_reason = "stop", message = new { content = output, refusal = (string?)null } } }, usage = new { total_tokens = 123 } });
        });
        var result = await Service(handler).RunAsync(AgentTestData.Context(), false, CancellationToken.None);
        result!.Output!.Action.Should().Be("answer"); result.Tokens.Should().Be(123);
        using var sent = JsonDocument.Parse(body!);
        sent.RootElement.GetProperty("tools").GetArrayLength().Should().Be(4);
        sent.RootElement.GetProperty("response_format").GetProperty("json_schema").GetProperty("strict").GetBoolean().Should().BeTrue();
        sent.RootElement.GetProperty("max_completion_tokens").GetInt32().Should().Be(16000);
        sent.RootElement.TryGetProperty("reasoning_effort", out _).Should().BeFalse();
        body.Should().NotContain("test-key");
    }

    // TEST-04: Requests after recommendation results use the configured completion budget and optional reasoning effort; covers no configured effort and low effort.
    /// <summary>Reserves the configured completion budget after recommendation results and preserves optional provider controls.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("low")]
    public async Task RunAsync_AfterRecommendation_UsesConfiguredCompletionBudget(string? effort)
    {
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
        var context = AgentTestData.Context();
        context.Exchanges.Add(new([new("call-reco", "get_recommendation", JsonSerializer.SerializeToElement(new { }))],
            [JsonSerializer.SerializeToElement(new { rankingMode = "personalized", items = new[] { new { dishId = 123, name = "Món cay" } } })]));
        var result = await Service(handler, limits: new() { MaxOutputTokens = 12000, ReasoningEffort = effort })
            .RunAsync(context, false, CancellationToken.None);
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
        var handler = new Handler(async request =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            body.RootElement.GetProperty("model").GetString().Should().Be("gpt-6-luna");
            body.RootElement.GetProperty("reasoning_effort").GetString().Should().Be("none");
            body.RootElement.GetProperty("tools").GetArrayLength().Should().Be(4);
            return Response(new { choices = new[] { new { finish_reason = "stop", message = new { content = Output() } } } });
        });
        var result = await Service(handler, limits: new() { ReasoningEffort = configuredEffort }, model: "gpt-6-luna")
            .RunAsync(AgentTestData.Context(), false, CancellationToken.None);
        result!.Output!.Action.Should().Be("answer");
        handler.Calls.Should().Be(1);
    }

    // TEST-06: A tool call whose completion was truncated is discarded as Truncated without executing its arguments or retrying.
    /// <summary>Never executes an apparently complete tool call when the provider reports an incomplete completion.</summary>
    [Fact]
    public async Task RunAsync_TruncatedToolCall_DiscardsArgumentsWithoutRetry()
    {
        var handler = new Handler(_ => Task.FromResult(Response(new
        {
            choices = new[] { new { finish_reason = "length", message = new { content = (string?)null,
                tool_calls = new[] { new { id = "call-cut", type = "function", function = new { name = "get_roadmap", arguments = "{\"roadmapId\":88}" } } } } } },
            usage = new { total_tokens = 17000 }
        })));
        var result = await Service(handler).RunAsync(AgentTestData.Context(), false, CancellationToken.None);
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
        var output = area switch { "json" => "{", "missing" => "{}", "extra" => Output().TrimEnd('}') + ",\"userId\":99}",
            "enum" => Output().Replace("answer", "delete_roadmap"), "duplicate" => Output().TrimEnd('}') + ",\"action\":\"answer\"}", _ => Output() };
        var handler = new Handler(_ => Task.FromResult(area == "http" ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) :
            area == "choices" ? Response(new { choices = Array.Empty<object>() }) : Response(new
            {
                choices = new[] { new { finish_reason = area == "truncated" ? "length" : "stop", message = new { content = output, refusal = area == "refusal" ? "refused" : null } } }
            })));
        var result = await Service(handler).RunAsync(AgentTestData.Context(), false, CancellationToken.None);
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
        var output = area switch { "json" => "{", "missing" => "{}", "extra" => Output().TrimEnd('}') + ",\"userId\":99}",
            "invented" => Output().TrimEnd('}') + ",\"private prompt text\":1}", "enum" => Output().Replace("answer", "delete_roadmap"),
            _ => Output().Replace("\"mealSlot\":null", "\"mealSlot\":\"brunch\"") };
        var handler = new Handler(_ => Task.FromResult(Response(new
            { choices = new[] { new { finish_reason = "stop", message = new { content = output, refusal = (string?)null } } } })));

        var result = await Service(handler).RunAsync(AgentTestData.Context(), false, CancellationToken.None);

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
        var calls = Enumerable.Range(0, area == "count" ? 7 : 1).Select(index => ToolCall($"call-{index}", area)).ToArray();
        var handler = new Handler(_ => Task.FromResult(Response(new { choices = new[] { new
            { finish_reason = area == "finish" ? "stop" : "tool_calls", message = new { tool_calls = calls } } } })));

        var result = await Service(handler).RunAsync(AgentTestData.Context(), false, CancellationToken.None);

        result!.Failure!.Kind.Should().Be(AgentLlmFailureKind.InvalidSchema);
        result.Failure.Reason.Should().Be(reason).And.NotContain("private");
        result.Tools.Should().BeEmpty();
    }

    // TEST-10: A context with tools closed after a duplicate request sends tool_choice=none before the round budget is spent.
    /// <summary>A repeated identical tool request closes tools for the next call even before the round budget is spent.</summary>
    [Fact]
    public async Task RunAsync_ToolsClosed_ForcesFinalOutput()
    {
        var handler = new Handler(async request =>
        {
            using var sent = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            sent.RootElement.GetProperty("tool_choice").GetString().Should().Be("none");
            return Response(new { choices = new[] { new { finish_reason = "stop", message = new { content = Output(), refusal = (string?)null } } } });
        });
        var context = AgentTestData.Context(); context.ToolsClosed = true;

        var result = await Service(handler).RunAsync(context, false, CancellationToken.None);

        result!.Output!.Action.Should().Be("answer");
        handler.Calls.Should().Be(1);
    }

    // TEST-11: Preserve provider tool call IDs and parse only allowlisted strictly validated arguments.
    /// <summary>Verifies tool-call/result correlation needed by subsequent provider rounds.</summary>
    [Fact]
    public async Task RunAsync_ToolCall_PreservesProviderIdentity()
    {
        var handler = new Handler(_ => Task.FromResult(Response(new { choices = new[] { new { finish_reason = "tool_calls", message = new
        { tool_calls = new[] { new { id = "call-owned", type = "function", function = new { name = "get_roadmap", arguments = "{\"roadmapId\":88}" } } } } } } })));
        var result = await Service(handler).RunAsync(AgentTestData.Context(), false, CancellationToken.None);
        result!.Tools.Single().Id.Should().Be("call-owned"); result.Tools.Single().Name.Should().Be("get_roadmap");
    }

    // TEST-12: Missing provider credentials avoid HTTP calls and caller cancellation is never converted to fallback.
    /// <summary>Verifies configuration and cancellation boundaries.</summary>
    [Fact]
    public async Task RunAsync_NoCredentialsOrCancellation_RespectsBoundary()
    {
        var handler = new Handler((_, ct) => Task.FromCanceled<HttpResponseMessage>(ct));
        var service = Service(handler, "");
        (await service.RunAsync(AgentTestData.Context(), false, CancellationToken.None))!.Failure!.Kind.Should().Be(AgentLlmFailureKind.Configuration); handler.Calls.Should().Be(0);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var act = () => Service(handler).RunAsync(AgentTestData.Context(), false, cancelled.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // TEST-13: Gemini requires its opaque tool signature on the subsequent assistant message.
    /// <summary>Checks a real parse/replay boundary and keeps provider metadata out of stored tool transcripts.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunAsync_ToolRoundTrip_PreservesOptionalProviderMetadata(bool signed)
    {
        var call = new Dictionary<string, object?> { ["id"] = "call-signed", ["type"] = "function",
            ["function"] = new { name = "get_roadmap", arguments = "{\"roadmapId\":88}" } };
        if (signed) call["extra_content"] = new { google = new { thought_signature = "opaque-provider-signature" } };
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
        var service = Service(handler);
        var context = AgentTestData.Context();
        var first = await service.RunAsync(context, false, CancellationToken.None);
        first.Should().NotBeNull();
        first!.Tools.Single().ProviderExtraContent.HasValue.Should().Be(signed);
        AgentJson.Write(first.Tools).Should().NotContain("opaque-provider-signature").And.NotContain("providerExtraContent");
        context.Exchanges.Add(new(first.Tools, [JsonSerializer.SerializeToElement(new { roadmapId = 88 })]));
        var second = await service.RunAsync(context, false, CancellationToken.None);
        second!.Output!.Action.Should().Be("answer");
        handler.Calls.Should().Be(2);
    }

    // TEST-14: Escaped Unicode in nested JSON can make a provider return literal escape sequences as province names.
    /// <summary>Preserves readable Vietnamese inside both context and tool data messages without changing persisted JSON.</summary>
    [Fact]
    public async Task RunAsync_VietnameseContextAndToolResult_RemainReadable()
    {
        var handler = new Handler(async request =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var messages = body.RootElement.GetProperty("messages");
            messages[1].GetProperty("content").GetString().Should().Contain("Hà Nội");
            messages[3].GetProperty("content").GetString().Should().Contain("Phở bò Hà Nội");
            return Response(new { choices = new[] { new { finish_reason = "stop", message = new { content = Output() } } } });
        });
        var context = AgentTestData.Context();
        context.State = context.State with { Province = "Hà Nội" };
        context.Exchanges.Add(new([new("call-vi", "get_roadmap", JsonSerializer.SerializeToElement(new { roadmapId = 88 }))],
            [JsonSerializer.SerializeToElement(new { dishName = "Phở bò Hà Nội" }, AgentJson.Options)]));
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
        var handler = new Handler(_ =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("private-key-and-prompt-must-not-leak") };
            response.Headers.RetryAfter = new(TimeSpan.FromSeconds(45)); return Task.FromResult(response);
        });
        var result = await Service(handler).RunAsync(AgentTestData.Context(), false, CancellationToken.None);
        result!.Failure.Should().Be(new AgentLlmFailure(kind, status, 45));
        AgentJson.Write(result).Should().NotContain("private-key").And.NotContain("test-key"); handler.Calls.Should().Be(1);
    }

    // TEST-16: A high-demand 503 keeps only the provider code and the high_demand reason, never the raw message.
    /// <summary>Classifies provider overload safely without leaking upstream error text.</summary>
    [Fact]
    public async Task RunAsync_GeminiHighDemand_PreservesSafeReasonWithoutRawMessage()
    {
        var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        { Content = new StringContent("{\"error\":{\"status\":\"UNAVAILABLE\",\"message\":\"This model is experiencing high demand. private-key-must-not-leak\"}}") }));
        var result = await Service(handler).RunAsync(AgentTestData.Context(), false, CancellationToken.None);
        result!.Failure!.ProviderCode.Should().Be("UNAVAILABLE"); result.Failure.Reason.Should().Be("high_demand");
        AgentJson.Write(result).Should().NotContain("private-key");
    }

    // TEST-17: Budget gateway denials map to a non-retryable configuration failure with a quota message; covers budget exhausted, spending stopped, ledger unavailable and gateway required.
    /// <summary>Keeps spending-control failures distinct from provider outages so they are never retried.</summary>
    [Theory]
    [InlineData(429, "gateway_budget_exhausted")]
    [InlineData(503, "gateway_spending_stopped")]
    [InlineData(503, "gateway_ledger_unavailable")]
    [InlineData(503, "ai_budget_gateway_required")]
    public async Task RunAsync_BudgetControlFailure_IsNotARetryableProviderOutage(int status, string code)
    {
        var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) {
            Content = new StringContent(JsonSerializer.Serialize(new { error = new { code, message = "private diagnostic" } })) }));
        var result = await Service(handler).RunAsync(AgentTestData.Context(), false, CancellationToken.None);
        result!.Failure!.Kind.Should().Be(AgentLlmFailureKind.Configuration);
        result.Failure.Reason.Should().Be("budget_control");
        var userError = new ANGI.Application.Common.Exceptions.AgentProviderException(result.Failure);
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
        var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent(body) }));
        var result = await Service(handler).RunAsync(AgentTestData.Context(), false, CancellationToken.None);
        result!.Failure!.Kind.Should().Be(AgentLlmFailureKind.RateLimited);
        result.Failure.HttpStatus.Should().Be(429);
    }

    // TEST-19: A Gemini array-wrapped 429 yields RESOURCE_EXHAUSTED, the rate_limit reason and a rounded-up retry delay.
    /// <summary>Reads Gemini's array error envelope so short limits can be retried at the right time.</summary>
    [Fact]
    public async Task RunAsync_GeminiArrayRateLimit_ReadsStatusAndRetryInfo()
    {
        var body = "[{\"error\":{\"code\":429,\"status\":\"RESOURCE_EXHAUSTED\",\"message\":\"Quota exceeded: GenerateRequestsPerMinutePerProject\",\"details\":[{\"retryDelay\":\"12.5s\"}]}}]";
        var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent(body) }));
        var result = await Service(handler).RunAsync(AgentTestData.Context(), false, CancellationToken.None);
        result!.Failure.Should().Be(new AgentLlmFailure(AgentLlmFailureKind.RateLimited, 429, 13, "RESOURCE_EXHAUSTED", "rate_limit"));
    }

    // TEST-20: HttpClient timeout and a transport exception remain distinct from received malformed output.
    /// <summary>Exercises cancellation-aware fake transport without making any network request.</summary>
    [Theory]
    [InlineData(true)][InlineData(false)]
    public async Task RunAsync_TransportFailure_IsNotSchemaFailure(bool timeout)
    {
        var handler = new Handler(async (_, ct) =>
        {
            if (timeout) await Task.Delay(Timeout.Infinite, ct);
            throw new HttpRequestException("sensitive internal endpoint");
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://provider.test/v1/"), Timeout = TimeSpan.FromMilliseconds(100) };
        var service = new OpenAiAgentLlmService(http, Options.Create(new AiSettings { ApiKey = "test-key", Model = "test-model" }), new(), TimeProvider.System);
        var result = await service.RunAsync(AgentTestData.Context(), false, CancellationToken.None);
        result!.Failure!.Kind.Should().Be(timeout ? AgentLlmFailureKind.Timeout : AgentLlmFailureKind.Network);
        AgentJson.Write(result).Should().NotContain("sensitive"); handler.Calls.Should().Be(1);
    }

    // TEST-21: Date-based retry hints use the injected clock and malformed or expired hints are ignored.
    /// <summary>Checks deterministic retry guidance without weakening HTTP failure classification.</summary>
    [Theory]
    [InlineData("future", 30)][InlineData("past", null)][InlineData("invalid", null)]
    public async Task RunAsync_RetryAfter_UsesSafeClockBasedDelay(string hint, int? expected)
    {
        var clock = new FixedClock();
        var handler = new Handler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            if (hint == "invalid") response.Headers.TryAddWithoutValidation("Retry-After", "not-a-date");
            else response.Headers.RetryAfter = new(clock.GetUtcNow().AddSeconds(hint == "future" ? 30 : -30));
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://provider.test/v1/") };
        var service = new OpenAiAgentLlmService(http, Options.Create(new AiSettings { ApiKey = "test-key", Model = "test-model" }), new(), clock);
        var result = await service.RunAsync(AgentTestData.Context(), false, CancellationToken.None);
        result!.Failure!.Kind.Should().Be(AgentLlmFailureKind.RateLimited); result.Failure.RetryAfterSeconds.Should().Be(expected);
    }

    // TEST-22: An invalid provider envelope retains HTTP 200 but is not misclassified as received model content needing repair.
    /// <summary>Distinguishes protocol corruption from malformed JSON produced inside a valid model response.</summary>
    [Fact]
    public async Task RunAsync_InvalidEnvelope_PreservesStatusWithoutSchemaRepair()
    {
        var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not-valid-provider-json") }));
        var result = await Service(handler).RunAsync(AgentTestData.Context(), false, CancellationToken.None);
        result!.Failure.Should().Be(new AgentLlmFailure(AgentLlmFailureKind.InvalidResponse, 200)); handler.Calls.Should().Be(1);
    }

    /// <summary>Supplies an exact clock for HTTP-date retry guidance.</summary>
    private sealed class FixedClock : TimeProvider
    {
        /// <summary>Returns the deterministic instant used to interpret provider HTTP dates.</summary>
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 28, 0, 0, 0, TimeSpan.Zero);
    }

    // TEST-23: Gateway rate and concurrency limits are temporary rate limits that keep the gateway's Retry-After, never spending-control
    // failures; covers gateway_rate_limit and gateway_concurrency_limit.
    /// <summary>Lets the orchestrator wait and retry a per-minute or in-flight gateway limit instead of telling the user to contact an administrator.</summary>
    [Theory]
    [InlineData("gateway_rate_limit", 7)]
    [InlineData("gateway_concurrency_limit", 3)]
    public async Task RunAsync_GatewayTemporaryLimit_IsRetryableRateLimit(string code, int retryAfter)
    {
        var handler = new Handler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) {
                Content = new StringContent(JsonSerializer.Serialize(new { error = new { message = "AI request blocked by spending controls.", type = "budget_control", code } })) };
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(retryAfter));
            return Task.FromResult(response);
        });

        var result = await Service(handler).RunAsync(AgentTestData.Context(), false, CancellationToken.None);

        result!.Failure.Should().Be(new AgentLlmFailure(AgentLlmFailureKind.RateLimited, 429, retryAfter, code, "rate_limit"));
        new ANGI.Application.Common.Exceptions.AgentProviderException(result.Failure!).Message.Should().NotContain("quản trị viên");
    }

    // TEST-24: Every request tells the model its scope and offers the chat and out_of_scope actions with suggestedDish in the strict schema.
    /// <summary>Without these the model has no way to decline "hướng dẫn bóc cam" and falls back to answer or clarify.</summary>
    [Fact]
    public async Task RunAsync_Request_DeclaresScopeAndSmallTalkActions()
    {
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

    /// <summary>Creates the strict response object while keeping every nullable field explicit.</summary>
    private static string Output() => JsonSerializer.Serialize(new { action = "answer", reply = "Explanation", state = (object?)null,
        suggestedDish = (string?)null, clarification = (string?)null,
        constraintsChangeConfirmed = false, baseProposalVersion = (int?)null, scope = new { dayNumber = (short?)null, mealSlot = (string?)null }, items = Array.Empty<object>(), changes = Array.Empty<object>() });
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
