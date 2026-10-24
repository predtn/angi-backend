using System.Net;
using System.Text;
using System.Text.Json;
using ANGI.Application.Common.Models.Roadmap;
using ANGI.Domain.Enums;
using ANGI.Infrastructure.Services.Ai;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ANGI.UnitTests.Infrastructure.Services.Ai;

public sealed class OpenAiRoadmapPlannerServiceTests
{
    private static readonly RoadmapPlanRequest Request = new(
        2,
        [MealSlot.Breakfast, MealSlot.LateNight],
        [
            new RoadmapPlanCandidate(1501, "Bún bò Huế", "Bún bò O Cương", 45000, ["Món nước"], 0),
            new RoadmapPlanCandidate(1502, "Chè Huế", "Chè Hẻm", 20000, ["Tráng miệng"], 0.42)
        ],
        ["Món nước"],
        "Ít cay");

    // TEST-01: Post the day with a strict schema limited to the requested meals and the cluster dishes, then map the plan.
    [Fact]
    public async Task PlanDayAsync_WithSuccessfulCompletion_ShouldSendSchemaAndMapPlan()
    {
        var handler = new StubHttpMessageHandler(_ => Completion(
            "{\"items\":[{\"meal_slot\":\"breakfast\",\"dish_id\":1501},{\"meal_slot\":\"late_night\",\"dish_id\":1502}]}"));

        var result = await CreateService(handler).PlanDayAsync(Request, CancellationToken.None);

        result.Should().BeEquivalentTo(new RoadmapPlanResult(
            "gpt-5.6-luna",
            [new RoadmapPlanAssignment(MealSlot.Breakfast, 1501), new RoadmapPlanAssignment(MealSlot.LateNight, 1502)]));
        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Post);
        request.Uri.Should().Be(new Uri("https://ai.angi.test/v1/chat/completions"));
        request.Authorization.Should().Be("Bearer test-ai-key");
        using var body = JsonDocument.Parse(request.Body!);
        body.RootElement.GetProperty("model").GetString().Should().Be("gpt-5.6-luna");
        body.RootElement.TryGetProperty("temperature", out _).Should().BeFalse("newer models reject any non-default temperature");
        var schema = body.RootElement.GetProperty("response_format").GetProperty("json_schema");
        schema.GetProperty("strict").GetBoolean().Should().BeTrue();
        var item = schema.GetProperty("schema").GetProperty("properties").GetProperty("items").GetProperty("items").GetProperty("properties");
        item.GetProperty("meal_slot").GetProperty("enum").EnumerateArray().Select(value => value.GetString()).Should().Equal("breakfast", "late_night");
        item.GetProperty("dish_id").GetProperty("enum").EnumerateArray().Select(value => value.GetInt32()).Should().Equal(1501, 1502);
        using var day = JsonDocument.Parse(body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
        day.RootElement.GetProperty("day_number").GetInt32().Should().Be(2);
        day.RootElement.GetProperty("note").GetString().Should().Be("Ít cay");
        day.RootElement.GetProperty("candidates")[1].GetProperty("distance_km_from_anchor").GetDouble().Should().Be(0.42);
    }

    // TEST-02: Return no plan, without throwing, whenever the AI cannot answer usefully or is not configured.
    [Theory]
    [InlineData("server-error")]
    [InlineData("rate-limited")]
    [InlineData("network")]
    [InlineData("timeout")]
    [InlineData("bad-body")]
    [InlineData("bad-content")]
    [InlineData("unknown-meal")]
    [InlineData("empty-content")]
    [InlineData("not-configured")]
    public async Task PlanDayAsync_WhenAiCannotAnswer_ShouldReturnNull(string failure)
    {
        var handler = new StubHttpMessageHandler(_ => failure switch
        {
            "server-error" => new HttpResponseMessage(HttpStatusCode.InternalServerError),
            "rate-limited" => new HttpResponseMessage(HttpStatusCode.TooManyRequests),
            "network" => throw new HttpRequestException("unreachable"),
            "timeout" => throw new TaskCanceledException("timeout"),
            "bad-body" => Json("not json"),
            "bad-content" => Completion("{\"items\":"),
            "unknown-meal" => Completion("{\"items\":[{\"meal_slot\":\"brunch\",\"dish_id\":1501}]}"),
            "empty-content" => Completion(""),
            _ => Completion("{\"items\":[]}")
        });

        var result = await CreateService(handler, failure == "not-configured" ? "" : "test-ai-key").PlanDayAsync(Request, CancellationToken.None);

        result.Should().BeNull();
        if (failure == "not-configured")
        {
            handler.Requests.Should().BeEmpty();
        }
    }

    // TEST-03: Let the caller's cancellation stop the job instead of falling back.
    [Fact]
    public async Task PlanDayAsync_WhenCallerCancels_ShouldRethrow()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new StubHttpMessageHandler(_ =>
        {
            cancellation.Cancel();
            throw new TaskCanceledException();
        });

        var act = () => CreateService(handler).PlanDayAsync(Request, cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static OpenAiRoadmapPlannerService CreateService(HttpMessageHandler handler, string apiKey = "test-ai-key") =>
        new(
            new HttpClient(handler) { BaseAddress = new Uri("https://ai.angi.test/v1/") },
            Options.Create(new AiSettings { BaseUrl = "https://ai.angi.test/v1/", ApiKey = apiKey, Model = "gpt-5.6-luna", TimeoutSeconds = 30 }),
            NullLogger<OpenAiRoadmapPlannerService>.Instance);

    private static HttpResponseMessage Completion(string content) => Json(JsonSerializer.Serialize(new
    {
        model = "gpt-5.6-luna",
        choices = new[] { new { message = new { role = "assistant", content } } }
    }));

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed record CapturedRequest(HttpMethod Method, Uri? Uri, string? Authorization, string? Body);

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _send;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> send)
        {
            _send = send;
        }

        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new CapturedRequest(
                request.Method,
                request.RequestUri,
                request.Headers.Authorization?.ToString(),
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
            return _send(request);
        }
    }
}
