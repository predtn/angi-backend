using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Services.Recommendation;
using ANGI.Application.Common.Models.Recommendation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ANGI.Infrastructure.Services.Recommendation
{
    /// <summary>
    /// Typed client of the recommendation service: snake_case JSON, header X-Api-Key, one timeout for every call.
    /// Failures become the outcomes of project_architecture.md §8.
    /// </summary>
    public sealed class RecommendationService : IRecommendationService
    {
        // RECO-05 accepts at most 2000 candidates and returns at most 200 dishes; RECO-06 takes at most 200 ids.
        internal const int MaxCandidates = 2000;
        internal const int MaxTopK = 200;
        internal const int DefaultTopK = 50;
        internal const int MaxImpressionDishes = 200;
        internal const int MaxSurveyAnswers = 50;

        private const string ApiKeyHeader = "X-Api-Key";
        private const string UnavailableMessage = "Hệ thống gợi ý tạm thời không khả dụng. Vui lòng thử lại sau.";

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
        };

        private readonly HttpClient _httpClient;
        private readonly RecommendationSettings _settings;
        private readonly ILogger<RecommendationService> _logger;

        public RecommendationService(
            HttpClient httpClient,
            IOptions<RecommendationSettings> settings,
            ILogger<RecommendationService> logger)
        {
            _httpClient = httpClient;
            _settings = settings.Value;
            _logger = logger;
        }

        private bool IsConfigured => !string.IsNullOrWhiteSpace(_settings.BaseUrl) && _httpClient.BaseAddress is not null;

        /// <inheritdoc />
        public async Task<SurveySubmissionResult> SubmitSurveyAsync(
            int userId,
            IReadOnlyList<SurveyAnswer> answers,
            DateTime? occurredAt,
            CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(answers);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(answers.Count, MaxSurveyAnswers, nameof(answers));

            if (!IsConfigured)
            {
                _logger.LogError("Recommendation:BaseUrl is not set; RECO-03 cannot be called.");
                throw Unavailable();
            }

            var body = new SurveyRequest(
                answers.Select(answer => new SurveyAnswerBody(answer.OptionCode, answer.ScaleValue)).ToArray(),
                occurredAt);

            var outcome = await SendAsync(HttpMethod.Post, $"users/{userId}/survey", body, "RECO-03", ct);
            if (outcome.Failed)
            {
                throw Unavailable();
            }

            using var response = outcome.Response!;
            if (response.IsSuccessStatusCode)
            {
                var result = await ReadAsync<SurveyResponse>(response, "RECO-03", ct) ?? throw Unavailable();
                return new SurveySubmissionResult(result.SurveyAnswerCount, result.HasVector, result.SurveyAt);
            }

            var errorCode = await ReadErrorCodeAsync(response, ct);
            if (response.StatusCode == HttpStatusCode.Conflict && errorCode == "SURVEY_ALREADY_SUBMITTED")
            {
                throw new ConflictException("SURVEY_ALREADY_SUBMITTED", "Bạn đã làm hoặc đã bỏ qua khảo sát sở thích.");
            }

            if (response.StatusCode == HttpStatusCode.UnprocessableEntity && errorCode == "UNKNOWN_OPTION_CODE")
            {
                throw new BadRequestException("UNKNOWN_OPTION_CODE", "Có lựa chọn khảo sát không hợp lệ.");
            }

            if ((int)response.StatusCode >= 500)
            {
                LogFailure("RECO-03", response.StatusCode, errorCode);
                throw Unavailable();
            }

            throw UnexpectedClientError("RECO-03", response.StatusCode, errorCode);
        }

        /// <inheritdoc />
        public async Task<RecommendationResult> RecommendAsync(
            int? userId,
            IReadOnlyList<int> candidateDishIds,
            int? topK,
            RecommendationContext context,
            CancellationToken ct)
            => await RankAsync(userId, candidateDishIds, topK, context, null, ct);

        public Task<RecommendationResult> RecommendConversationAsync(int userId, IReadOnlyList<int> candidates,
            int topK, RecommendationVector vector, CancellationToken ct) =>
            RankAsync(userId, candidates, topK, RecommendationContext.RoadmapAi, vector, ct);

        private async Task<RecommendationResult> RankAsync(int? userId, IReadOnlyList<int> candidateDishIds,
            int? topK, RecommendationContext context, RecommendationVector? vector, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(candidateDishIds);
            if (topK is not null)
            {
                ArgumentOutOfRangeException.ThrowIfLessThan(topK.Value, 1, nameof(topK));
                ArgumentOutOfRangeException.ThrowIfGreaterThan(topK.Value, MaxTopK, nameof(topK));
            }

            // The backend sends the first 2000 in its rating order (sheet "Tổng quan", Giới hạn 2000 ứng viên).
            var candidates = candidateDishIds.Count > MaxCandidates
                ? candidateDishIds.Take(MaxCandidates).ToArray()
                : candidateDishIds;
            if (candidates.Count == 0)
            {
                return new RecommendationResult(null, Array.Empty<int>(), Array.Empty<int>());
            }

            if (!IsConfigured)
            {
                return Fallback(candidates, topK);
            }

            var body = new RecommendRequest(userId, candidates, topK, context, vector?.VectorSource, vector?.UserVectorOverride);
            var outcome = await SendAsync(HttpMethod.Post, "recommendations", body, "RECO-05", ct);
            if (outcome.Failed)
            {
                return Fallback(candidates, topK);
            }

            using var response = outcome.Response!;
            if (response.IsSuccessStatusCode)
            {
                var result = await ReadAsync<RecommendResponse>(response, "RECO-05", ct);
                if (result?.Items is null || vector is not null && result.VectorSource != vector.VectorSource)
                {
                    if (vector is not null && result?.VectorSource != vector.VectorSource)
                        _logger.LogWarning("RECO-05 did not confirm vector source {VectorSource}; using rating fallback.", vector.VectorSource);
                    return Fallback(candidates, topK);
                }

                var items = result.Items.OrderBy(item => item.Rank).ToArray();
                return new RecommendationResult(
                    result.RequestId,
                    items.Select(item => item.DishId).ToArray(),
                    result.IgnoredDishIds ?? Array.Empty<int>(),
                    items.Length == 0 && !result.RequestId.HasValue
                        ? null
                        : items.Select(item => item.RecommendScore).ToArray(), result.VectorSource);
            }

            var errorCode = await ReadErrorCodeAsync(response, ct);
            if ((int)response.StatusCode >= 500)
            {
                LogFailure("RECO-05", response.StatusCode, errorCode);
                return Fallback(candidates, topK);
            }

            throw UnexpectedClientError("RECO-05", response.StatusCode, errorCode);
        }

        /// <inheritdoc />
        public async Task RecordImpressionsAsync(
            int userId,
            Guid requestId,
            IReadOnlyList<int> dishIds,
            DateTime? shownAt,
            CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(dishIds);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(dishIds.Count, MaxImpressionDishes, nameof(dishIds));
            if (dishIds.Count == 0 || !IsConfigured)
            {
                return;
            }

            var body = new ImpressionsRequest(userId, requestId, dishIds, shownAt);
            var outcome = await SendAsync(HttpMethod.Post, "impressions", body, "RECO-06", ct);
            if (outcome.Failed)
            {
                return;
            }

            using var response = outcome.Response!;
            if (!response.IsSuccessStatusCode)
            {
                LogFailure("RECO-06", response.StatusCode, await ReadErrorCodeAsync(response, ct));
            }
        }

        /// <inheritdoc />
        public async Task<SurveyStatus?> GetSurveyStatusAsync(int userId, CancellationToken ct)
        {
            if (!IsConfigured)
            {
                return null;
            }

            var outcome = await SendAsync(HttpMethod.Get, $"users/{userId}/survey", null, "RECO-09", ct);
            if (outcome.Failed)
            {
                return null;
            }

            using var response = outcome.Response!;
            if (!response.IsSuccessStatusCode)
            {
                LogFailure("RECO-09", response.StatusCode, await ReadErrorCodeAsync(response, ct));
                return null;
            }

            var result = await ReadAsync<SurveyStatusResponse>(response, "RECO-09", ct);
            return result is null
                ? null
                : new SurveyStatus(result.Completed, result.SurveyAt, result.SurveyAnswerCount);
        }

        public async Task<UserPreferenceSnapshot?> GetPreferencesAsync(int userId, CancellationToken ct)
        {
            if (!IsConfigured) return null;
            var outcome = await SendAsync(HttpMethod.Get, $"users/{userId}/preferences", (object?)null, "RECO-PREFERENCES", ct);
            if (outcome.Failed) return null;
            using var response = outcome.Response!;
            if (!response.IsSuccessStatusCode)
            {
                LogFailure("RECO-PREFERENCES", response.StatusCode, await ReadErrorCodeAsync(response, ct));
                return null;
            }
            var snapshot = await ReadAsync<UserPreferenceSnapshot>(response, "RECO-PREFERENCES", ct);
            return snapshot is not null && snapshot.UserId == userId && snapshot.OntologyVersion == 1 &&
                (snapshot.Vec is null || snapshot.Vec.Length == 12 && snapshot.Vec.All(double.IsFinite)) ? snapshot : null;
        }

        /// <summary>
        /// Sends one request. A timeout or network error is logged and reported as Failed; the caller's own
        /// cancellation is rethrown.
        /// </summary>
        private async Task<SendOutcome> SendAsync(
            HttpMethod method,
            string path,
            object? body,
            string endpointId,
            CancellationToken ct)
        {
            using var request = new HttpRequestMessage(method, path);
            if (body is not null)
            {
                request.Content = JsonContent.Create(body, body.GetType(), options: _jsonOptions);
            }

            if (!string.IsNullOrWhiteSpace(_settings.ApiKey))
            {
                request.Headers.Add(ApiKeyHeader, _settings.ApiKey);
            }

            try
            {
                return new SendOutcome(await _httpClient.SendAsync(request, ct));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning("{EndpointId} did not answer within {TimeoutMilliseconds} ms.", endpointId, _settings.TimeoutMilliseconds);
            }
            catch (HttpRequestException exception)
            {
                _logger.LogWarning(exception, "{EndpointId} could not be reached.", endpointId);
            }

            return SendOutcome.Failure;
        }

        /// <summary>Reads a 2xx body; an unreadable body is logged and returned as null.</summary>
        private async Task<T?> ReadAsync<T>(HttpResponseMessage response, string endpointId, CancellationToken ct)
            where T : class
        {
            try
            {
                return await response.Content.ReadFromJsonAsync<T>(_jsonOptions, ct);
            }
            catch (Exception exception) when (exception is JsonException or NotSupportedException)
            {
                _logger.LogWarning(exception, "{EndpointId} returned a body that could not be read.", endpointId);
                return null;
            }
        }

        /// <summary>Reads error_code from { "error_code", "message", "errors" }; reco is branched on error_code only.</summary>
        private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response, CancellationToken ct)
        {
            try
            {
                var error = await response.Content.ReadFromJsonAsync<ErrorResponse>(_jsonOptions, ct);
                return error?.ErrorCode;
            }
            catch (Exception exception) when (exception is JsonException or NotSupportedException)
            {
                return null;
            }
        }

        private static RecommendationResult Fallback(IReadOnlyList<int> candidates, int? topK) =>
            new(null, candidates.Take(topK ?? DefaultTopK).ToArray(), Array.Empty<int>());

        private static ServiceUnavailableException Unavailable() =>
            new("SERVICE_UNAVAILABLE", UnavailableMessage);

        private void LogFailure(string endpointId, HttpStatusCode statusCode, string? errorCode) =>
            _logger.LogWarning(
                "{EndpointId} failed with status {StatusCode} and error code {ErrorCode}.",
                endpointId,
                (int)statusCode,
                errorCode);

        /// <summary>A 4xx the API Design does not list (wrong X-Api-Key, 422 VALIDATION_FAILED): a bug, so 500.</summary>
        private InvalidOperationException UnexpectedClientError(string endpointId, HttpStatusCode statusCode, string? errorCode)
        {
            _logger.LogError(
                "{EndpointId} rejected the request with status {StatusCode} and error code {ErrorCode}.",
                endpointId,
                (int)statusCode,
                errorCode);
            return new InvalidOperationException(
                $"{endpointId} rejected the request with status {(int)statusCode} ({errorCode ?? "no error code"}).");
        }

        private sealed record SendOutcome(HttpResponseMessage? Response)
        {
            public static readonly SendOutcome Failure = new((HttpResponseMessage?)null);

            public bool Failed => Response is null;
        }

        // Bodies of sheet "Reco API (nội bộ)", serialized snake_case; null fields are left out.
        private sealed record SurveyAnswerBody(string OptionCode, decimal? ScaleValue);

        private sealed record SurveyRequest(IReadOnlyList<SurveyAnswerBody> Answers, DateTime? OccurredAt);

        private sealed record SurveyResponse(int UserId, int SurveyAnswerCount, bool HasVector, DateTime SurveyAt);

        private sealed record RecommendRequest(
            int? UserId,
            IReadOnlyList<int> CandidateDishIds,
            int? TopK,
            RecommendationContext Context,
            string? VectorSource = null,
            double[]? UserVectorOverride = null);

        private sealed record RecommendItem(int DishId, int Rank, double RecommendScore);

        private sealed record RecommendResponse(
            Guid? RequestId,
            int ConfigVersion,
            IReadOnlyList<RecommendItem>? Items,
            IReadOnlyList<int>? IgnoredDishIds,
            string? VectorSource = null);

        private sealed record ImpressionsRequest(int UserId, Guid RequestId, IReadOnlyList<int> DishIds, DateTime? ShownAt);

        private sealed record SurveyStatusResponse(int UserId, bool Completed, DateTime? SurveyAt, int SurveyAnswerCount);

        private sealed record ErrorResponse(string? ErrorCode, string? Message);
    }
}
