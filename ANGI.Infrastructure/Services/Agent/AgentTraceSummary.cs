using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ANGI.Infrastructure.Services.Agent;

/// <summary>How bad one trace event is, so the summary can list problems first.</summary>
public enum AgentTraceLevel { Info, Warning, Error }

/// <summary>Turns one Agent turn's events into a short text a person can read: problems first, then one line per event.</summary>
/// <remarks>The full JSON of the same turn goes to the detail file; this text only points at what went wrong and where.</remarks>
public static partial class AgentTraceSummary
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private static readonly HashSet<string> Errors =
    [
        "turn_failed", "error", "failed", "tool_failed", "tool_failed_detail", "commit_failed",
        "invalid_output", "state_rejected", "state_rejected_detail", "proposal_unusable"
    ];

    // A wrong tool argument goes back to the model, which usually fixes it on the next call, so it is a warning, not a failure.
    private static readonly HashSet<string> Warnings =
    [
        "tool_argument_error", "validation", "validation_ignored", "duplicate_tool", "llm_retry", "empty_reply", "misunderstood", "ignored_items",
        "context_shrunk", "save_skipped", "state_change_unstaged", "out_of_edit_meals_restored"
    ];

    private static readonly string[] RepairFlags = ["schemaRepaired", "proposalRepaired", "emptyRepaired", "coverageRepaired"];

    /// <summary>One event as the summary sees it.</summary>
    public sealed record Event(double AtMs, string Name, JsonNode? Data, string? At);

    /// <summary>Error for failures, Warning for things the Agent had to fix or skip, Info otherwise.</summary>
    public static AgentTraceLevel LevelOf(string name, JsonNode? data)
    {
        if (Errors.Contains(name)) return AgentTraceLevel.Error;
        if (name == "llm")
        {
            if (Text(data, "outcome") is not null || Text(data, "failureKind") is not null) return AgentTraceLevel.Error;
            if (Bool(data, "repair") || RepairFlags.Any(flag => Bool(data, flag))) return AgentTraceLevel.Warning;
            return AgentTraceLevel.Info;
        }
        if (name == "validation") return Count(data, "errors") > 0 ? AgentTraceLevel.Warning : AgentTraceLevel.Info;
        return Warnings.Contains(name) ? AgentTraceLevel.Warning : AgentTraceLevel.Info;
    }

    /// <summary>The first ANGI method on the current stack outside the trace code, e.g. "AgentOrchestrator.RunAsync (AgentOrchestrator.cs:218)".</summary>
    public static string? CallSite()
    {
        foreach (var frame in new StackTrace(1, true).GetFrames())
        {
            var method = frame.GetMethod();
            var type = method?.DeclaringType;
            if (method is null || type is null) continue;
            var name = method.Name;
            // Async methods and lambdas run in compiler-made nested types such as "<RunAsync>d__12"; show the real method.
            while (type.DeclaringType is not null && type.Name.StartsWith('<'))
            {
                var real = CompilerName().Match(type.Name);
                if (real.Success) name = real.Groups[1].Value;
                type = type.DeclaringType;
            }
            var lambda = CompilerName().Match(name);
            if (lambda.Success) name = lambda.Groups[1].Value;
            if (type.Namespace?.StartsWith("ANGI.", StringComparison.Ordinal) != true) continue;
            if (type == typeof(AgentTelemetry) || type == typeof(AgentTraceSummary)) continue;
            var file = frame.GetFileName();
            return file is null ? $"{type.Name}.{name}" : $"{type.Name}.{name} ({FileName(file)}:{frame.GetFileLineNumber()})";
        }
        return null;
    }

    /// <summary>Builds the readable block for one turn.</summary>
    public static string Build(string header, long? attemptId, string outcome, double totalMs, IReadOnlyList<Event> events, string detailFile)
    {
        var levels = events.Select(item => LevelOf(item.Name, item.Data)).ToArray();
        var problems = events.Select((item, index) => (item, level: levels[index])).Where(pair => pair.level != AgentTraceLevel.Info).ToArray();
        var llmCalls = events.Where(item => item.Name == "llm").ToArray();
        var tokens = llmCalls.Sum(item => Number(item.Data, "tokens") ?? 0);
        var errorCount = problems.Count(pair => pair.level == AgentTraceLevel.Error);
        var warningCount = problems.Length - errorCount;
        var status = outcome switch
        {
            "failed" => "✗ THẤT BẠI",
            "completed" when errorCount > 0 => $"! XONG nhưng có {errorCount} lỗi",
            "completed" when warningCount > 0 => $"✓ XONG ({warningCount} cảnh báo)",
            "completed" => "✓ XONG",
            _ => "? CHƯA KẾT THÚC"
        };

        var text = new StringBuilder();
        var rule = new string('═', 100);
        text.AppendLine(rule);
        text.AppendLine($"{header}  attempt={attemptId}   {status}   {Seconds(totalMs)} · {llmCalls.Length} lần gọi LLM · {tokens.ToString("N0", Invariant)} tokens");
        text.AppendLine(rule);

        var user = events.FirstOrDefault(item => item.Name == "user_message")?.Data;
        var reply = events.LastOrDefault(item => item.Name == "final_reply")?.Data;
        text.AppendLine($"Người dùng : {Shorten(Text(user, "content"), 200) ?? "(không có)"}{Suffix(Text(user, "command"))}");
        text.AppendLine($"Trả lời    : {Shorten(Text(reply, "reply"), 200) ?? "(không có câu trả lời)"}{Suffix(Text(reply, "action"))}");
        text.AppendLine();

        if (problems.Length == 0) text.AppendLine("VẤN ĐỀ: không có");
        else
        {
            text.AppendLine($"VẤN ĐỀ ({errorCount} lỗi, {warningCount} cảnh báo)");
            for (var index = 0; index < events.Count; index++)
            {
                if (levels[index] == AgentTraceLevel.Info) continue;
                var item = events[index];
                var said = Describe(item.Name, item.Data, full: true);
                text.AppendLine($"  {Mark(levels[index])} {Seconds(item.AtMs),8}  {item.Name}{(said.Length > 0 ? " — " + said : "")}");
                foreach (var line in Explain(item, events.Skip(index + 1))) text.AppendLine($"{"",14}{line}");
            }
        }
        text.AppendLine();

        text.AppendLine("DIỄN BIẾN");
        for (var index = 0; index < events.Count; index++)
        {
            var item = events[index];
            text.AppendLine($"  {Seconds(item.AtMs),8}  {Mark(levels[index])} {item.Name,-22} {Describe(item.Name, item.Data, full: false)}".TrimEnd());
        }
        text.AppendLine($"Chi tiết JSON: {detailFile} → tìm \"{header}\"");
        text.AppendLine();
        return text.ToString();
    }

    /// <summary>
    /// The file name of a path from a stack trace. The trace may come from another OS than the one reading it, so both
    /// '/' and '\' separate folders: Path.GetFileName on Linux would keep "E:\x\File.cs" whole.
    /// </summary>
    private static string FileName(string path) => path[(path.LastIndexOfAny(['/', '\\']) + 1)..];

    /// <summary>Extra lines under a problem: where it was recorded and, for an exception, its root cause and the ANGI call chain.</summary>
    private static IEnumerable<string> Explain(Event item, IEnumerable<Event> later)
    {
        if (item.Name == "validation" || item.Name == "validation_ignored")
            foreach (var error in Array(item.Data, "errors").Take(5)) yield return $"• {error}";

        // The arguments the model sent are in the tool_result written right after the argument error.
        if (item.Name == "tool_argument_error" && later.FirstOrDefault(next => next.Name == "tool_result")?.Data is { } result)
        {
            // "state" is the whole plan copied into the call; leaving it out shows the arguments that were actually checked.
            var arguments = result["arguments"]?.DeepClone();
            if (arguments is JsonObject fields && fields.Remove("state")) fields["state"] = "(bỏ qua, xem file chi tiết)";
            yield return $"model gọi {Text(result, "name")} với: {Shorten(Compact(arguments), 400)}";
        }

        var detail = Text(item.Data, "detail");
        if (detail is not null)
        {
            var causes = InnerException().Matches(detail);
            if (causes.Count > 0) yield return $"nguyên nhân gốc: {Shorten(causes[^1].Groups[1].Value.Trim(), 200)}";
            var frames = AngiFrame().Matches(detail)
                .Select(match => $"{ShortMethod(match.Groups[1].Value)} ({FileName(match.Groups[2].Value)}:{match.Groups[3].Value})")
                .ToArray();
            if (frames.Length > 0)
            {
                yield return $"ném ra tại {frames[0]}";
                if (frames.Length > 1) yield return $"được gọi từ {string.Join(" ← ", frames.Skip(1))}";
                yield break;
            }
        }
        if (item.At is not null) yield return $"ghi nhận tại {item.At}";
    }

    /// <summary>One short line for an event; the known events get their key fields, the rest a cut compact JSON.</summary>
    private static string Describe(string name, JsonNode? data, bool full)
    {
        if (data is null) return "";
        string Join(params string?[] parts) => string.Join(" · ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
        switch (name)
        {
            case "user_message":
                return $"\"{Shorten(Text(data, "content"), 120)}\"{Suffix(Text(data, "command"))}";
            case "llm":
                var repairs = RepairFlags.Where(flag => Bool(data, flag)).ToArray();
                return Join($"#{Number(data, "callNumber")}{(Bool(data, "repair") ? " (sửa lại)" : "")}",
                    Seconds(Number(data, "elapsedMs") ?? 0),
                    Number(data, "tokens") is { } tokens ? $"{tokens} tok ({Number(data, "cachedTokens") ?? 0} cached)" : null,
                    Text(data, "outcome"), Text(data, "failureKind"), Text(data, "reason"),
                    Number(data, "httpStatus") is { } http ? $"HTTP {http}" : null,
                    repairs.Length > 0 ? "đã sửa: " + string.Join(",", repairs) : null);
            case "llm_output":
                var output = data["output"];
                var tools = Nodes(data, "tools").Select(tool => Text(tool, "name")).ToArray();
                return Join($"#{Number(data, "callNumber")}",
                    tools.Length > 0 ? "gọi tool " + string.Join(",", tools) : null,
                    output is null ? null : $"→ {Text(output, "action")} {Text(output, "tool")}".TrimEnd(),
                    Shorten(Text(output, "reply"), 80) is { } said ? $"\"{said}\"" : null,
                    Text(data, "rawContent") is { } raw ? $"chuỗi thô sai schema: {Shorten(raw, 80)}" : null);
            case "model_output":
                return Join(Text(data, "phase"), $"{Text(data, "action")} {Text(data, "tool")}".TrimEnd(), $"reply {Number(data, "replyLength") ?? 0} ký tự");
            case "use_tool":
                return Text(data, "tool") ?? "";
            case "tool":
                return Join(Text(data, "name"), Seconds(Number(data, "elapsedMs") ?? 0));
            case "tool_result":
                var result = data["result"];
                var counts = result is JsonObject fields
                    ? fields.Where(field => field.Value is JsonArray).Select(field => $"{field.Key}={((JsonArray)field.Value!).Count}")
                    : [];
                return Join(Text(data, "name"), Text(result, "status"), string.Join(" ", counts));
            case "validation":
            case "validation_ignored":
                var first = Array(data, "errors").FirstOrDefault();
                return Join($"{Count(data, "errors")} lỗi", Count(data, "warnings") > 0 ? $"{Count(data, "warnings")} cảnh báo" : null,
                    full ? null : first);
            case "final_reply":
                return Join(Text(data, "action"), $"\"{Shorten(Text(data, "reply"), 100)}\"");
            case "turn_failed":
                return Text(data, "errorType") ?? "";
            case "error":
                return Shorten(Text(data, "message"), 200) ?? "";
            default:
                if (Text(data, "message") is { } message) return Shorten(message, full ? 300 : 140)!;
                var json = Compact(data);
                return json is "{}" ? "" : Shorten(json, full ? 300 : 140)!;
        }
    }

    /// <summary>One-line JSON with Vietnamese kept readable.</summary>
    private static string Compact(JsonNode? node) => node?.ToJsonString(CompactJson) ?? "null";

    private static readonly System.Text.Json.JsonSerializerOptions CompactJson =
        new(AgentTraceWriter.Json) { WriteIndented = false };

    private static string Mark(AgentTraceLevel level) => level switch { AgentTraceLevel.Error => "✗", AgentTraceLevel.Warning => "!", _ => " " };

    private static string Seconds(double ms) => (ms / 1000).ToString("0.000", Invariant) + "s";

    private static string Suffix(string? value) => value is null ? "" : $"   ({value})";

    private static string? Shorten(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return null;
        var flat = Regex.Replace(value, @"\s+", " ").Trim();
        return flat.Length <= max ? flat : flat[..max] + "…";
    }

    private static string ShortMethod(string full)
    {
        var parts = full.Split('.');
        return parts.Length >= 2 ? $"{parts[^2]}.{parts[^1]}" : full;
    }

    private static string? Text(JsonNode? node, string key) =>
        node is JsonObject obj && obj[key] is JsonValue value ? value.ToString() is { Length: > 0 } text ? text : null : null;

    private static bool Bool(JsonNode? node, string key) =>
        node is JsonObject obj && obj[key] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    private static double? Number(JsonNode? node, string key) =>
        node is JsonObject obj && obj[key] is JsonValue value && value.TryGetValue<double>(out var number) ? number : null;

    private static int Count(JsonNode? node, string key) => node is JsonObject obj && obj[key] is JsonArray array ? array.Count : 0;

    private static IEnumerable<string> Array(JsonNode? node, string key) =>
        node is JsonObject obj && obj[key] is JsonArray array ? array.Select(item => item?.ToString() ?? "") : [];

    private static IEnumerable<JsonNode?> Nodes(JsonNode? node, string key) =>
        node is JsonObject obj && obj[key] is JsonArray array ? array : [];

    [GeneratedRegex(@"^<([^>]+)>")]
    private static partial Regex CompilerName();

    [GeneratedRegex(@"--->\s*([^\r\n]+)")]
    private static partial Regex InnerException();

    [GeneratedRegex(@"at (ANGI\.[^\(\r\n]+)\([^\r\n]*?\) in ([^\r\n]+?):line (\d+)")]
    private static partial Regex AngiFrame();
}
