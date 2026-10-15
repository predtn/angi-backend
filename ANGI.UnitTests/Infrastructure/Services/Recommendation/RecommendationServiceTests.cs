using System.Net;
using System.Text;
using System.Text.Json;
using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Models.Recommendation;
using ANGI.Infrastructure.Services.Recommendation;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ANGI.UnitTests.Infrastructure.Services.Recommendation
{
    public sealed class RecommendationServiceTests
    {
        private static readonly Guid RequestId = Guid.Parse("6f0c2b1e-8d7a-4c1e-9b3f-2a5d7e9c1f00");

        // TEST-01: Read the RECO-09 survey status and send the configured internal API key.
        [Fact]
        public async Task GetSurveyStatusAsync_WithSuccessfulResponse_ShouldReturnStatus()
        {
            var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK,
                "{\"user_id\":12,\"completed\":true,\"survey_at\":\"2026-10-02T02:30:01Z\",\"survey_answer_count\":0}"));
            var service = CreateService(handler);

            var status = await service.GetSurveyStatusAsync(12, CancellationToken.None);

            status.Should().Be(new SurveyStatus(true, new DateTime(2026, 10, 2, 2, 30, 1, DateTimeKind.Utc), 0));
            handler.Requests.Should().ContainSingle();
            handler.Requests[0].Method.Should().Be(HttpMethod.Get);
            handler.Requests[0].Uri.Should().Be(new Uri("https://reco.angi.test/users/12/survey"));
            handler.Requests[0].ApiKey.Should().Be("test-api-key");
        }

        // TEST-02: Return an unknown survey status when RECO-09 fails, times out or reco is not configured.
        [Theory]
        [InlineData("server-error")]
        [InlineData("client-error")]
        [InlineData("timeout")]
        [InlineData("network")]
        [InlineData("bad-body")]
        [InlineData("not-configured")]
        public async Task GetSurveyStatusAsync_WhenRecoCannotAnswer_ShouldReturnNull(string failure)
        {
            var service = CreateFailingService(failure);

            var status = await service.GetSurveyStatusAsync(12, CancellationToken.None);

            status.Should().BeNull();
        }

        // TEST-03: Send RECO-03 answers in snake_case and map the stored survey.
        [Fact]
        public async Task SubmitSurveyAsync_WithAnswers_ShouldPostSnakeCaseBodyAndMapResult()
        {
            var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.Created,
                "{\"user_id\":12,\"survey_answer_count\":2,\"has_vector\":true,\"survey_at\":\"2026-10-02T02:30:01Z\"}"));
            var service = CreateService(handler);

            var result = await service.SubmitSurveyAsync(
                12,
                [new SurveyAnswer("broth_rich", 0.8m), new SurveyAnswer("spicy_high")],
                new DateTime(2026, 10, 2, 2, 30, 0, DateTimeKind.Utc),
                CancellationToken.None);

            result.Should().Be(new SurveySubmissionResult(2, true, new DateTime(2026, 10, 2, 2, 30, 1, DateTimeKind.Utc)));
            var request = handler.Requests.Should().ContainSingle().Subject;
            request.Method.Should().Be(HttpMethod.Post);
            request.Uri.Should().Be(new Uri("https://reco.angi.test/users/12/survey"));
            using var body = JsonDocument.Parse(request.Body!);
            body.RootElement.GetProperty("answers")[0].GetProperty("option_code").GetString().Should().Be("broth_rich");
            body.RootElement.GetProperty("answers")[0].GetProperty("scale_value").GetDecimal().Should().Be(0.8m);
            body.RootElement.GetProperty("answers")[1].TryGetProperty("scale_value", out _).Should().BeFalse();
            body.RootElement.GetProperty("occurred_at").GetString().Should().Be("2026-10-02T02:30:00Z");
        }

        // TEST-04: Send an empty answer list for PREF-03 (skip).
        [Fact]
        public async Task SubmitSurveyAsync_WithNoAnswers_ShouldPostEmptyList()
        {
            var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.Created,
                "{\"user_id\":12,\"survey_answer_count\":0,\"has_vector\":false,\"survey_at\":\"2026-10-02T02:30:01Z\"}"));
            var service = CreateService(handler);

            var result = await service.SubmitSurveyAsync(12, [], null, CancellationToken.None);

            result.AnswerCount.Should().Be(0);
            result.HasVector.Should().BeFalse();
            using var body = JsonDocument.Parse(handler.Requests[0].Body!);
            body.RootElement.GetProperty("answers").GetArrayLength().Should().Be(0);
            body.RootElement.TryGetProperty("occurred_at", out _).Should().BeFalse();
        }

        // TEST-05: Map the RECO-03 error codes listed in the API Design to the backend error codes.
        [Theory]
        [InlineData(HttpStatusCode.Conflict, "SURVEY_ALREADY_SUBMITTED", typeof(ConflictException))]
        [InlineData(HttpStatusCode.UnprocessableEntity, "UNKNOWN_OPTION_CODE", typeof(BadRequestException))]
        public async Task SubmitSurveyAsync_WithListedError_ShouldThrowBackendErrorCode(
            HttpStatusCode statusCode,
            string errorCode,
            Type exceptionType)
        {
            var service = CreateService(new StubHttpMessageHandler(_ => Json(statusCode,
                $"{{\"error_code\":\"{errorCode}\",\"message\":\"x\"}}")));

            var act = () => service.SubmitSurveyAsync(12, [new SurveyAnswer("broth_rich")], null, CancellationToken.None);

            var exception = (await act.Should().ThrowAsync<AppException>()).Which;
            exception.Should().BeOfType(exceptionType);
            exception.ErrorCode.Should().Be(errorCode);
        }

        // TEST-06: Return 503 SERVICE_UNAVAILABLE when RECO-03 fails, times out or reco is not configured.
        [Theory]
        [InlineData("server-error")]
        [InlineData("timeout")]
        [InlineData("network")]
        [InlineData("bad-body")]
        [InlineData("not-configured")]
        public async Task SubmitSurveyAsync_WhenRecoFails_ShouldThrowServiceUnavailable(string failure)
        {
            var service = CreateFailingService(failure);

            var act = () => service.SubmitSurveyAsync(12, [new SurveyAnswer("broth_rich")], null, CancellationToken.None);

            (await act.Should().ThrowAsync<ServiceUnavailableException>()).Which.ErrorCode.Should().Be("SERVICE_UNAVAILABLE");
        }

        // TEST-07: Treat a RECO-03 4xx the API Design does not list (wrong X-Api-Key, VALIDATION_FAILED) as a bug: 500.
        [Theory]
        [InlineData(HttpStatusCode.Unauthorized, "UNAUTHORIZED")]
        [InlineData(HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED")]
        [InlineData(HttpStatusCode.Conflict, "SOMETHING_ELSE")]
        public async Task SubmitSurveyAsync_WithUnlistedClientError_ShouldThrowInternalError(
            HttpStatusCode statusCode,
            string errorCode)
        {
            var service = CreateService(new StubHttpMessageHandler(_ => Json(statusCode,
                $"{{\"error_code\":\"{errorCode}\",\"message\":\"x\"}}")));

            var act = () => service.SubmitSurveyAsync(12, [new SurveyAnswer("broth_rich")], null, CancellationToken.None);

            await act.Should().ThrowExactlyAsync<InvalidOperationException>();
        }

        // TEST-08: Send RECO-05 in snake_case and return dish ids by rank with their scores, the request id and ignored ids.
        [Fact]
        public async Task RecommendAsync_WithSuccessfulResponse_ShouldReturnDishesByRank()
        {
            var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK, """
                {"request_id":"6f0c2b1e-8d7a-4c1e-9b3f-2a5d7e9c1f00","config_version":3,
                 "items":[{"dish_id":133,"rank":2,"recommend_score":0.7,"final_score":0.7,"explicit_score":0.5,"wilson":0.6,"exploration":0},
                          {"dish_id":105,"rank":1,"recommend_score":0.8,"final_score":0.8,"explicit_score":0.9,"wilson":0.6,"exploration":0}],
                 "ignored_dish_ids":[140]}
                """));
            var service = CreateService(handler);

            var result = await service.RecommendAsync(12, [105, 120, 133, 140], 50, RecommendationContext.RoadmapManual, CancellationToken.None);

            result.RequestId.Should().Be(RequestId);
            result.DishIds.Should().Equal(105, 133);
            result.Scores.Should().Equal(0.8, 0.7);
            result.IgnoredDishIds.Should().Equal(140);
            var request = handler.Requests.Should().ContainSingle().Subject;
            request.Uri.Should().Be(new Uri("https://reco.angi.test/recommendations"));
            request.ApiKey.Should().Be("test-api-key");
            using var body = JsonDocument.Parse(request.Body!);
            body.RootElement.GetProperty("user_id").GetInt32().Should().Be(12);
            body.RootElement.GetProperty("candidate_dish_ids").EnumerateArray().Select(id => id.GetInt32())
                .Should().Equal(105, 120, 133, 140);
            body.RootElement.GetProperty("top_k").GetInt32().Should().Be(50);
            body.RootElement.GetProperty("context").GetString().Should().Be("roadmap_manual");
        }

        // TEST-09: Leave user_id and top_k out for a Guest request without top_k.
        [Fact]
        public async Task RecommendAsync_ForGuest_ShouldOmitUserIdAndTopK()
        {
            var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK,
                "{\"request_id\":null,\"config_version\":3,\"items\":[{\"dish_id\":105,\"rank\":1}],\"ignored_dish_ids\":[]}"));
            var service = CreateService(handler);

            var result = await service.RecommendAsync(null, [105], null, RecommendationContext.Home, CancellationToken.None);

            result.RequestId.Should().BeNull();
            result.DishIds.Should().Equal(105);
            using var body = JsonDocument.Parse(handler.Requests[0].Body!);
            body.RootElement.TryGetProperty("user_id", out _).Should().BeFalse();
            body.RootElement.TryGetProperty("top_k", out _).Should().BeFalse();
            body.RootElement.GetProperty("context").GetString().Should().Be("home");
        }

        // TEST-10: Fall back to the candidates in the order sent, cut to top_k and without scores, when RECO-05 fails or reco is not configured.
        [Theory]
        [InlineData("server-error")]
        [InlineData("timeout")]
        [InlineData("network")]
        [InlineData("bad-body")]
        [InlineData("not-configured")]
        public async Task RecommendAsync_WhenRecoFails_ShouldReturnCandidatesInOrder(string failure)
        {
            var service = CreateFailingService(failure);

            var result = await service.RecommendAsync(12, [140, 105, 133, 120], 3, RecommendationContext.List, CancellationToken.None);

            result.RequestId.Should().BeNull();
            result.DishIds.Should().Equal(140, 105, 133);
            result.Scores.Should().BeNull();
            result.IgnoredDishIds.Should().BeEmpty();
        }

        // TEST-11: Cut the fallback to reco's default top_k of 50.
        [Fact]
        public async Task RecommendAsync_FallbackWithoutTopK_ShouldReturnFiftyDishes()
        {
            var service = CreateFailingService("server-error");

            var result = await service.RecommendAsync(12, Enumerable.Range(1, 80).ToArray(), null, RecommendationContext.List, CancellationToken.None);

            result.DishIds.Should().Equal(Enumerable.Range(1, 50));
        }

        // TEST-12: Send only the first 2000 candidates, which RECO-05 accepts at most.
        [Fact]
        public async Task RecommendAsync_WithMoreThan2000Candidates_ShouldSendTheFirst2000()
        {
            var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK,
                "{\"request_id\":null,\"config_version\":3,\"items\":[],\"ignored_dish_ids\":[]}"));
            var service = CreateService(handler);

            await service.RecommendAsync(12, Enumerable.Range(1, 2500).ToArray(), 200, RecommendationContext.List, CancellationToken.None);

            using var body = JsonDocument.Parse(handler.Requests[0].Body!);
            var sent = body.RootElement.GetProperty("candidate_dish_ids").EnumerateArray().Select(id => id.GetInt32()).ToArray();
            sent.Should().Equal(Enumerable.Range(1, 2000));
        }

        // TEST-13: Return an empty result without calling reco when there is no candidate.
        [Fact]
        public async Task RecommendAsync_WithNoCandidate_ShouldNotCallReco()
        {
            var handler = new StubHttpMessageHandler(_ => throw new InvalidOperationException("Must not be called."));
            var service = CreateService(handler);

            var result = await service.RecommendAsync(12, [], 10, RecommendationContext.List, CancellationToken.None);

            result.DishIds.Should().BeEmpty();
            result.RequestId.Should().BeNull();
            handler.Requests.Should().BeEmpty();
        }

        // TEST-14: Turn a RECO-05 4xx (422 VALIDATION_FAILED, wrong X-Api-Key) into a 500 instead of hiding it with the fallback.
        [Theory]
        [InlineData(HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED")]
        [InlineData(HttpStatusCode.Unauthorized, "UNAUTHORIZED")]
        public async Task RecommendAsync_WithClientError_ShouldThrowInternalError(HttpStatusCode statusCode, string errorCode)
        {
            var service = CreateService(new StubHttpMessageHandler(_ => Json(statusCode,
                $"{{\"error_code\":\"{errorCode}\",\"message\":\"x\"}}")));

            var act = () => service.RecommendAsync(12, [105], 10, RecommendationContext.List, CancellationToken.None);

            await act.Should().ThrowExactlyAsync<InvalidOperationException>();
        }

        // TEST-15: Reject a top_k outside 1..200.
        [Theory]
        [InlineData(0)]
        [InlineData(201)]
        public async Task RecommendAsync_WithTopKOutOfRange_ShouldThrowArgumentOutOfRange(int topK)
        {
            var service = CreateService(new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK, "{}")));

            var act = () => service.RecommendAsync(12, [105], topK, RecommendationContext.List, CancellationToken.None);

            await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
        }

        // TEST-16: Send RECO-06 impressions in snake_case.
        [Fact]
        public async Task RecordImpressionsAsync_ShouldPostSnakeCaseBody()
        {
            var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK,
                "{\"request_id\":\"6f0c2b1e-8d7a-4c1e-9b3f-2a5d7e9c1f00\",\"counted\":[105],\"ignored\":[133]}"));
            var service = CreateService(handler);

            await service.RecordImpressionsAsync(12, RequestId, [105, 133], new DateTime(2026, 10, 2, 2, 15, 0, DateTimeKind.Utc), CancellationToken.None);

            var request = handler.Requests.Should().ContainSingle().Subject;
            request.Uri.Should().Be(new Uri("https://reco.angi.test/impressions"));
            using var body = JsonDocument.Parse(request.Body!);
            body.RootElement.GetProperty("user_id").GetInt32().Should().Be(12);
            body.RootElement.GetProperty("request_id").GetGuid().Should().Be(RequestId);
            body.RootElement.GetProperty("dish_ids").EnumerateArray().Select(id => id.GetInt32()).Should().Equal(105, 133);
            body.RootElement.GetProperty("shown_at").GetString().Should().Be("2026-10-02T02:15:00Z");
        }

        // TEST-17: Only log RECO-06 failures, including 404 REQUEST_NOT_FOUND and 403 FORBIDDEN, and never throw.
        [Theory]
        [InlineData("server-error")]
        [InlineData("client-error")]
        [InlineData("timeout")]
        [InlineData("network")]
        [InlineData("not-configured")]
        public async Task RecordImpressionsAsync_WhenRecoFails_ShouldNotThrow(string failure)
        {
            var service = CreateFailingService(failure);

            var act = () => service.RecordImpressionsAsync(12, RequestId, [105], null, CancellationToken.None);

            await act.Should().NotThrowAsync();
        }

        // TEST-18: Rethrow the caller's own cancellation instead of falling back.
        [Fact]
        public async Task RecommendAsync_WhenCallerCancels_ShouldThrowOperationCanceled()
        {
            using var cts = new CancellationTokenSource();
            var service = CreateService(new StubHttpMessageHandler(_ =>
            {
                cts.Cancel();
                throw new TaskCanceledException("Canceled");
            }));

            var act = () => service.RecommendAsync(12, [105], 10, RecommendationContext.List, cts.Token);

            await act.Should().ThrowAsync<OperationCanceledException>();
        }

        private static RecommendationService CreateFailingService(string failure) => failure switch
        {
            "server-error" => CreateService(new StubHttpMessageHandler(_ => Json(HttpStatusCode.ServiceUnavailable,
                "{\"error_code\":\"INTERNAL_ERROR\",\"message\":\"x\"}"))),
            "client-error" => CreateService(new StubHttpMessageHandler(_ => Json(HttpStatusCode.NotFound,
                "{\"error_code\":\"REQUEST_NOT_FOUND\",\"message\":\"x\"}"))),
            "timeout" => CreateService(new StubHttpMessageHandler(_ => throw new TaskCanceledException("Timeout"))),
            "network" => CreateService(new StubHttpMessageHandler(_ => throw new HttpRequestException("Unavailable"))),
            "bad-body" => CreateService(new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK, "not json"))),
            "not-configured" => CreateService(
                new StubHttpMessageHandler(_ => throw new InvalidOperationException("Must not be called.")),
                baseUrl: ""),
            _ => throw new ArgumentOutOfRangeException(nameof(failure))
        };

        private static RecommendationService CreateService(HttpMessageHandler handler, string baseUrl = "https://reco.angi.test/")
        {
            var client = new HttpClient(handler);
            if (baseUrl.Length > 0)
            {
                client.BaseAddress = new Uri(baseUrl);
            }

            return new RecommendationService(
                client,
                Options.Create(new RecommendationSettings
                {
                    BaseUrl = baseUrl,
                    ApiKey = "test-api-key",
                    TimeoutMilliseconds = 800
                }),
                NullLogger<RecommendationService>.Instance);
        }

        private static HttpResponseMessage Json(HttpStatusCode statusCode, string body) =>
            new(statusCode) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        private sealed record CapturedRequest(HttpMethod Method, Uri? Uri, string? ApiKey, string? Body);

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
                    request.Headers.TryGetValues("X-Api-Key", out var values) ? values.Single() : null,
                    request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
                return _send(request);
            }
        }
    }
}
