using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ANGI.Application.Common.Interfaces.Services.Recommendation;
using Microsoft.Extensions.Options;

namespace ANGI.Infrastructure.Services.Recommendation
{
    public sealed class RecommendationService : IRecommendationService
    {
        private readonly HttpClient _httpClient;
        private readonly RecommendationSettings _settings;

        public RecommendationService(
            HttpClient httpClient,
            IOptions<RecommendationSettings> settings)
        {
            _httpClient = httpClient;
            _settings = settings.Value;
        }

        public async Task<bool?> GetSurveyCompletionAsync(int userId, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(_settings.BaseUrl) || _httpClient.BaseAddress is null)
            {
                return null;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, $"users/{userId}/survey");
            if (!string.IsNullOrWhiteSpace(_settings.ApiKey))
            {
                request.Headers.Add("X-Api-Key", _settings.ApiKey);
            }

            try
            {
                using var response = await _httpClient.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                var result = await response.Content.ReadFromJsonAsync<SurveyStatusResponse>(cancellationToken: ct);
                return result?.Completed;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return null;
            }
            catch (HttpRequestException)
            {
                return null;
            }
            catch (JsonException)
            {
                return null;
            }
            catch (NotSupportedException)
            {
                return null;
            }
        }

        private sealed class SurveyStatusResponse
        {
            [JsonPropertyName("completed")]
            public bool Completed { get; set; }
        }
    }
}
