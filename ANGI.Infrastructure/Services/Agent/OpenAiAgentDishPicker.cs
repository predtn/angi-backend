using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ANGI.Application.Common.Interfaces.Services.Agent;
using ANGI.Application.Common.Models.Agent;
using ANGI.Infrastructure.Services.Ai;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace ANGI.Infrastructure.Services.Agent;

/// <summary>
/// Asks the model which of the filtered dishes fit the user's wish, with the list grouped by restaurant. A repeated request (same wish, same list) is
/// answered from memory, so a starter button pressed by many users costs one call until the catalog changes.
/// </summary>
public sealed class OpenAiAgentDishPicker(HttpClient http, IOptions<AiSettings> settings, IMemoryCache cache) : IAgentDishPicker
{
    /// <summary>How long a pick for the same wish and list is reused.</summary>
    private static readonly TimeSpan CacheFor = TimeSpan.FromHours(6);

    // Measured 2026-11-27 on gpt-6-luna (20 labelled requests, 3 runs): no invented ids, Hà Nội specialties instead of abalone and ramen.
    private const string Rules = """
        Bạn là bước chọn món của ANGI. Backend đã lọc sẵn danh sách món theo điều kiện cứng (nơi, bữa, giá, loại món). Việc của bạn: đọc mong muốn của người dùng và chọn những món trong danh sách hợp nhất, kể cả ý mềm như "đặc trưng Hà Nội", "độc lạ", "no", "rẻ mà ngon", "thanh đạm", "mời khách".
        Quy tắc:
        - Chỉ dùng id có trong danh sách. Không bịa id.
        - best: món khớp rõ mong muốn, xếp từ hợp nhất. good: món hợp nhưng kém hơn. Tổng cộng không quá số món được yêu cầu.
        - Ít món thật sự hợp thì chọn ít; không thêm món không hợp chỉ để đủ số. Không món nào hợp thì cả hai rỗng.
        - Đa dạng: tránh nhiều món gần như giống nhau, tránh quá 2 món cùng một quán trừ khi người dùng hỏi quán đó.
        - Tên quán cho biết phong cách ẩm thực (Nhật, Hàn, Việt...); dùng nó cùng tên món.
        """;

    private static readonly object Schema = new
    {
        type = "object", additionalProperties = false, required = new[] { "best", "good" },
        properties = new
        {
            best = new { type = "array", items = new { type = "integer" } },
            good = new { type = "array", items = new { type = "integer" } }
        }
    };

    /// <inheritdoc />
    public async Task<AgentDishPick?> PickAsync(AgentDishPickRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.Value.ApiKey) || string.IsNullOrWhiteSpace(settings.Value.Model) || http.BaseAddress is null ||
            string.IsNullOrWhiteSpace(request.Wish) || request.Candidates.Count == 0) return null;
        var list = Render(request.Candidates);
        var key = CacheKey(request, list);
        if (cache.TryGetValue(key, out AgentDishPick? known) && known is not null) return known with { Tokens = 0 };
        try
        {
            // The list goes before the wish, so requests over the same list share a cached prompt prefix.
            var body = new Dictionary<string, object?>
            {
                ["model"] = settings.Value.Model,
                ["messages"] = new object[]
                {
                    new { role = "system", content = Rules },
                    new { role = "user", content = $"Danh sách món (id tên giá), gom theo quán:\n{list}\n\nMong muốn của người dùng: {request.Wish.Trim()}\nSố món tối đa: {request.MaxPicks}" }
                },
                ["max_completion_tokens"] = 600,
                ["response_format"] = new { type = "json_schema", json_schema = new { name = "angi_dish_pick", strict = true, schema = Schema } },
                ["prompt_cache_key"] = "angi-dish-pick"
            };
            // GPT-6 Luna runs with reasoning disabled, as in the main Agent call.
            if (settings.Value.Model == "gpt-6-luna") body["reasoning_effort"] = "none";
            using var message = new HttpRequestMessage(HttpMethod.Post, "chat/completions") { Content = JsonContent.Create(body) };
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.Value.ApiKey);
            using var response = await http.SendAsync(message, ct);
            if (!response.IsSuccessStatusCode) return null;
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = document.RootElement;
            var choice = root.GetProperty("choices")[0];
            if (choice.GetProperty("finish_reason").GetString() != "stop") return null;
            var content = choice.GetProperty("message").GetProperty("content").GetString();
            if (string.IsNullOrWhiteSpace(content)) return null;
            using var picked = JsonDocument.Parse(content);
            var tokens = root.TryGetProperty("usage", out var usage) && usage.TryGetProperty("total_tokens", out var total) ? total.GetInt32() : (int?)null;
            var pick = new AgentDishPick(Ids(picked.RootElement, "best"), Ids(picked.RootElement, "good"), tokens);
            cache.Set(key, pick, CacheFor);
            return pick;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException or IndexOutOfRangeException
            or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Restaurants with their dishes on one line each: the restaurant name is the cuisine clue the catalog has no tag for.</summary>
    public static string Render(IReadOnlyList<AgentPickCandidate> candidates) => string.Join('\n', candidates.GroupBy(dish => dish.Restaurant)
        .Select(group => $"# {group.Key}\n" + string.Join("; ", group.Select(dish =>
            $"{dish.Id} {dish.Name} {Math.Round(dish.Price / 1000m).ToString(CultureInfo.InvariantCulture)}k"))));

    private static int[] Ids(JsonElement root, string name) => root.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array
        ? array.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out _)).Select(item => item.GetInt32()).ToArray() : [];

    private string CacheKey(AgentDishPickRequest request, string list) =>
        "agent-dish-pick:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{settings.Value.Model}\n{request.Wish.Trim().ToLowerInvariant()}\n{request.MaxPicks}\n{list}")));
}
