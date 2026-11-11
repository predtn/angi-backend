using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ANGI.Infrastructure.Services.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace ANGI.Infrastructure.Services.Agent;

/// <summary>Writes the per-turn Agent trace (user text, raw model output, tool results, final reply) to a local daily file.</summary>
/// <remarks>
/// Only on in Development and when "AgentTrace:Enabled" is true, so real users' chats are never written on a server.
/// Files: "logs/agent-trace-yyyyMMdd.log" under the content root, kept for "AgentTrace:RetentionDays" days (default 7).
/// </remarks>
public sealed class AgentTraceWriter
{
    public const string Prefix = "agent-trace";
    private const int MaxEventCharacters = 30_000;
    private static readonly object Gate = new();
    private static DateTime _purgedDay;

    /// <summary>Readable JSON: Vietnamese kept as is, enums as names, nulls dropped.</summary>
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _directory;
    private readonly int _retentionDays;

    public AgentTraceWriter(IHostEnvironment environment, IConfiguration configuration)
    {
        Enabled = environment.IsDevelopment() && configuration.GetValue("AgentTrace:Enabled", false);
        _directory = Path.Combine(environment.ContentRootPath, configuration["AgentTrace:Directory"] ?? "logs");
        _retentionDays = Math.Clamp(configuration.GetValue("AgentTrace:RetentionDays", 7), 1, 365);
    }

    /// <summary>False outside Development or when switched off; callers then skip collecting anything.</summary>
    public bool Enabled { get; }

    /// <summary>Turns one event payload into JSON, cutting very large tool results so one turn stays readable.</summary>
    public static JsonNode? ToNode(object? data)
    {
        if (data is null) return null;
        try
        {
            var text = JsonSerializer.Serialize(data, Json);
            if (text.Length <= MaxEventCharacters) return JsonNode.Parse(text);
            return JsonValue.Create($"[cắt bớt, dài {text.Length} ký tự] " + text[..MaxEventCharacters]);
        }
        catch (Exception error) when (error is JsonException or NotSupportedException or InvalidOperationException)
        {
            return JsonValue.Create($"[không ghi được dữ liệu: {error.GetType().Name}]");
        }
    }

    /// <summary>Appends one finished turn: a header line to search for, then the indented JSON.</summary>
    public void Write(string header, JsonObject turn)
    {
        if (!Enabled) return;
        var now = DateTime.Now;
        var block = $"===== {header} ====={Environment.NewLine}{turn.ToJsonString(Json)}{Environment.NewLine}{Environment.NewLine}";
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(_directory);
                if (_purgedDay != now.Date)
                {
                    LogFileRetention.Purge(_directory, Prefix, _retentionDays, now);
                    _purgedDay = now.Date;
                }
                File.AppendAllText(LogFileRetention.PathFor(_directory, Prefix, now), block);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
