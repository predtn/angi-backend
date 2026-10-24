using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Models.Roadmap;
using ANGI.Domain.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ANGI.Infrastructure.Services.Ai;

/// <summary>
/// Schedules one roadmap day with OpenAI Chat Completions and structured output: the JSON schema only allows the
/// requested meals and the dish ids of the cluster. Any failure returns null so the job schedules the day itself.
/// </summary>
public sealed class OpenAiRoadmapPlannerService : IRoadmapPlannerService
{
    public const string Version = "rm10-v1";

    private const string SystemPrompt = """
        You plan one day of a Vietnamese food trip. You receive the meals to fill and candidate dishes, already ranked
        by a recommender (earlier = better match for the traveler) and all close to each other.
        Choose exactly one dish for each meal, or one per meal for as many meals as there are candidates, never the
        same dish twice. Prefer dishes that suit the meal time (lighter food for breakfast, full dishes for lunch and
        dinner, small bites for snack and late_night), dishes with the preferred tags, and the traveler's note. Among
        similar choices keep the better-ranked dish. Answer only with the JSON the schema describes.
        """;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _httpClient;
    private readonly AiSettings _settings;
    private readonly ILogger<OpenAiRoadmapPlannerService> _logger;

    public OpenAiRoadmapPlannerService(
        HttpClient httpClient,
        IOptions<AiSettings> settings,
        ILogger<OpenAiRoadmapPlannerService> logger)
    {
        _httpClient = httpClient;
        _settings = settings.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public string PromptVersion => Version;

    /// <inheritdoc />
    public async Task<RoadmapPlanResult?> PlanDayAsync(RoadmapPlanRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_settings.ApiKey) || string.IsNullOrWhiteSpace(_settings.Model) ||
            request.MealSlots.Count == 0 || request.Candidates.Count == 0)
        {
            return null;
        }

        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
            {
                Content = JsonContent.Create(BuildBody(request), options: Json)
            };
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey);
            using var response = await _httpClient.SendAsync(message, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "AI roadmap planning answered {StatusCode} ({Error}); the job plans the day itself.",
                    (int)response.StatusCode,
                    await ReadErrorAsync(response, ct));
                return null;
            }

            var completion = await response.Content.ReadFromJsonAsync<ChatCompletion>(Json, ct);
            var content = completion?.Choices?.FirstOrDefault()?.Message?.Content;
            if (string.IsNullOrWhiteSpace(content))
            {
                return null;
            }

            var plan = JsonSerializer.Deserialize<DayPlan>(content, Json);
            if (plan?.Items is null)
            {
                return null;
            }

            return new RoadmapPlanResult(
                completion!.Model ?? _settings.Model,
                plan.Items.Select(item => new RoadmapPlanAssignment(ParseMealSlot(item.MealSlot), item.DishId)).ToArray());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or ArgumentOutOfRangeException)
        {
            _logger.LogWarning(exception, "AI roadmap planning failed; the job plans the day itself.");
            return null;
        }
    }

    private object BuildBody(RoadmapPlanRequest request)
    {
        var meals = request.MealSlots.Select(ToApi).ToArray();
        var dishIds = request.Candidates.Select(candidate => candidate.DishId).ToArray();
        var day = new
        {
            day_number = request.DayNumber,
            meals,
            candidates = request.Candidates.Select(candidate => new
            {
                dish_id = candidate.DishId,
                name = candidate.DishName,
                restaurant = candidate.RestaurantName,
                price_vnd = candidate.Price,
                tags = candidate.Tags,
                distance_km_from_anchor = candidate.DistanceFromAnchorKm
            }),
            preferred_tags = request.PreferredTags,
            note = request.Note
        };

        return new
        {
            // No temperature: newer models only accept the default, and the strict schema already bounds the answer.
            model = _settings.Model,
            messages = new object[]
            {
                new { role = "system", content = SystemPrompt },
                new { role = "user", content = JsonSerializer.Serialize(day, Json) }
            },
            response_format = new
            {
                type = "json_schema",
                json_schema = new
                {
                    name = "roadmap_day_plan",
                    strict = true,
                    schema = new Dictionary<string, object>
                    {
                        ["type"] = "object",
                        ["additionalProperties"] = false,
                        ["required"] = new[] { "items" },
                        ["properties"] = new Dictionary<string, object>
                        {
                            ["items"] = new Dictionary<string, object>
                            {
                                ["type"] = "array",
                                ["items"] = new Dictionary<string, object>
                                {
                                    ["type"] = "object",
                                    ["additionalProperties"] = false,
                                    ["required"] = new[] { "meal_slot", "dish_id" },
                                    ["properties"] = new Dictionary<string, object>
                                    {
                                        ["meal_slot"] = new Dictionary<string, object> { ["type"] = "string", ["enum"] = meals },
                                        ["dish_id"] = new Dictionary<string, object> { ["type"] = "integer", ["enum"] = dishIds }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        };
    }

    /// <summary>OpenAI's error.message (e.g. an unsupported parameter); it never contains the key.</summary>
    private static async Task<string?> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var error = await response.Content.ReadFromJsonAsync<ErrorResponse>(Json, ct);
            return error?.Error?.Message;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ToApi(MealSlot meal) => JsonNamingPolicy.SnakeCaseLower.ConvertName(meal.ToString());

    private static MealSlot ParseMealSlot(string? value) => value switch
    {
        "breakfast" => MealSlot.Breakfast,
        "lunch" => MealSlot.Lunch,
        "dinner" => MealSlot.Dinner,
        "snack" => MealSlot.Snack,
        "late_night" => MealSlot.LateNight,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown meal slot from the AI.")
    };

    private sealed record ChatCompletion(string? Model, IReadOnlyList<ChatChoice>? Choices);

    private sealed record ChatChoice(ChatMessage? Message);

    private sealed record ChatMessage(string? Content);

    private sealed record DayPlan(IReadOnlyList<DayPlanItem>? Items);

    private sealed record DayPlanItem(string? MealSlot, int DishId);

    private sealed record ErrorResponse(ErrorDetail? Error);

    private sealed record ErrorDetail(string? Message);
}
