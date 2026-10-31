using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ANGI.AiGateway;

/// <summary>Allows text-only Chat Completions with bounded output and no separately billed hosted tools.</summary>
public sealed class RequestPolicy(GatewaySettings settings)
{
    private static readonly HashSet<string> Allowed = ["model", "messages", "max_tokens", "max_completion_tokens",
        "response_format", "tools", "tool_choice", "parallel_tool_calls", "reasoning_effort", "temperature", "n", "stream"];

    public PreparedRequest Prepare(string workload, byte[] input)
    {
        if (workload != "chat") throw new GatewayDenied("gateway_unauthorized", 401);
        if (input.Length > settings.ChatMaxBodyBytes) throw new GatewayDenied("gateway_request_too_large", 413);
        try
        {
            using var document = JsonDocument.Parse(input, new() { MaxDepth = 64 });
            ValidateUnique(document.RootElement);
            var root = JsonNode.Parse(input) as JsonObject ?? throw new GatewayDenied("gateway_invalid_request", 400);
            if (root.Any(pair => !Allowed.Contains(pair.Key)) || root["model"]?.GetValue<string>() != GatewaySettings.Model)
                throw new GatewayDenied("gateway_model_or_option_not_allowed", 400);
            if (root["stream"] is { } stream && stream.GetValue<bool>() || root["n"] is { } count && count.GetValue<int>() != 1)
                throw new GatewayDenied("gateway_multiple_or_streamed_outputs_not_allowed", 400);
            var messages = root["messages"] as JsonArray;
            if (messages is null || messages.Count is < 1 or > 128) throw new GatewayDenied("gateway_invalid_messages", 400);
            foreach (var node in messages)
            {
                var message = node as JsonObject ?? throw new GatewayDenied("gateway_invalid_messages", 400);
                if (message.Any(pair => pair.Key is not ("role" or "content" or "tool_calls" or "tool_call_id" or "name")) ||
                    message["role"]?.GetValue<string>() is not ("system" or "developer" or "user" or "assistant" or "tool") ||
                    message["content"] is { } content && content.GetValueKind() != JsonValueKind.String)
                    throw new GatewayDenied("gateway_only_text_messages_allowed", 400);
            }
            if (root["tools"] is { } toolNode)
            {
                if (toolNode is not JsonArray tools || tools.Count > 8 ||
                    tools.Any(tool => tool is not JsonObject obj || obj["type"]?.GetValue<string>() != "function" ||
                        obj.Any(pair => pair.Key is not ("type" or "function"))))
                    throw new GatewayDenied("gateway_hosted_tools_not_allowed", 400);
            }
            if (root["max_tokens"] is not null && root["max_completion_tokens"] is not null)
                throw new GatewayDenied("gateway_ambiguous_output_limit", 400);
            var maximum = settings.ChatMaxOutputTokens;
            var output = root["max_completion_tokens"]?.GetValue<int>() ?? root["max_tokens"]?.GetValue<int>() ?? maximum;
            if (output < 1 || output > maximum) throw new GatewayDenied("gateway_output_limit_exceeded", 400);
            var effort = root["reasoning_effort"]?.GetValue<string>() ?? "none";
            if (effort is not ("none" or "low")) throw new GatewayDenied("gateway_reasoning_effort_not_allowed", 400);
            if (root["tools"] is JsonArray { Count: > 0 } && effort != "none")
                throw new GatewayDenied("gateway_function_tools_require_no_reasoning", 400);
            root.Remove("max_tokens");
            root["max_completion_tokens"] = output;
            root["reasoning_effort"] = effort;
            // Reject client-controlled paid tiers/cache settings above, and force standard service here.
            root["service_tier"] = "default";
            root["stream"] = false;
            root["n"] = 1;
            var body = JsonSerializer.SerializeToUtf8Bytes(root);
            // Reserve the documented entire model context, not an unproven characters-to-tokens estimate.
            var reserved = Cost(GatewaySettings.ContextTokens, output);
            return new(workload, body, Convert.ToHexString(SHA256.HashData(body)), output, reserved, settings.PolicyHash);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new GatewayDenied("gateway_invalid_request", 400);
        }
    }

    public static CompletionUsage? ReadUsage(byte[] body, PreparedRequest request)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            ValidateUnique(document.RootElement);
            var root = document.RootElement;
            if (!root.TryGetProperty("usage", out var usage) ||
                !usage.TryGetProperty("prompt_tokens", out var inputField) || !inputField.TryGetInt32(out var input) ||
                !usage.TryGetProperty("completion_tokens", out var outputField) || !outputField.TryGetInt32(out var output) ||
                input < 0 || input > GatewaySettings.ContextTokens || output < 0 || output > request.MaxOutputTokens)
                return null;
            if (usage.TryGetProperty("total_tokens", out var total) && (!total.TryGetInt64(out var tokens) || tokens != (long)input + output)) return null;
            int? reasoning = null;
            if (usage.TryGetProperty("completion_tokens_details", out var details) && details.ValueKind != JsonValueKind.Null)
            {
                if (details.ValueKind != JsonValueKind.Object) return null;
                if (details.TryGetProperty("reasoning_tokens", out var reasoningField))
                {
                    if (!reasoningField.TryGetInt32(out var count) || count < 0 || count > output) return null;
                    reasoning = count;
                }
            }
            // completion_tokens already includes reasoning: never add it again.
            return new(input, output, reasoning, Cost(input, output));
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or GatewayDenied) { return null; }
    }

    public static decimal Cost(int input, int output) => decimal.Ceiling(
        (input * GatewaySettings.InputUsdPerMillion + output * GatewaySettings.OutputUsdPerMillion) / 1_000_000m * 10_000_000_000m) / 10_000_000_000m;

    private static void ValidateUnique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new GatewayDenied("gateway_duplicate_json_property", 400);
                ValidateUnique(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) ValidateUnique(item);
    }
}
