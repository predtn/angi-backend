using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ANGI.Application.Common.Interfaces.Services.Agent;
using Microsoft.Extensions.Logging;

namespace ANGI.Infrastructure.Services.Agent;

/// <summary>Emits structured, correlation-aware Agent metrics without raw messages or secrets.</summary>
/// <remarks>
/// In Development with the trace on, it also gathers every event of the request, plus the content passed to
/// <see cref="Trace"/>, and writes them as one block per turn when the request scope ends.
/// </remarks>
public sealed class AgentTelemetry(ILogger<AgentTelemetry> logger, AgentTraceWriter? trace = null) : IAgentTelemetry, IDisposable
{
    private readonly string _fallbackCorrelationId = Guid.NewGuid().ToString("N");
    private readonly List<TraceEvent> _events = [];
    private readonly long _started = Stopwatch.GetTimestamp();
    private bool _written;

    /// <summary>Records safe diagnostic metadata, resource revisions and timing for QA and operations.</summary>
    public void Record(string name, long conversationId, long? turnId, long? attemptId, object? data = null)
    {
        logger.LogInformation("Agent {Event} correlationId={CorrelationId} conversationId={ConversationId} turnId={TurnId} attemptId={AttemptId} data={Data}",
            name, CorrelationId, conversationId, turnId, attemptId, data is null ? null : JsonSerializer.Serialize(data));
        Collect(name, conversationId, turnId, attemptId, data);
    }

    /// <summary>Adds content to the Development trace only; never written to the normal log.</summary>
    public void Trace(string name, long conversationId, long? turnId, long? attemptId, object? data = null) =>
        Collect(name, conversationId, turnId, attemptId, data);

    /// <summary>Writes the gathered turns when the request scope ends, whatever way the turn finished.</summary>
    public void Dispose()
    {
        if (_written || trace is not { Enabled: true } || _events.Count == 0) return;
        _written = true;
        foreach (var turn in _events.Where(item => item.TurnId is not null).GroupBy(item => (item.ConversationId, item.TurnId)))
        {
            var events = turn.ToArray();
            var names = events.Select(item => item.Name).ToHashSet();
            var outcome = names.Contains("turn_failed") ? "failed" : names.Contains("completed") ? "completed" : "unfinished";
            var first = events[0];
            var body = new JsonObject
            {
                ["time"] = first.Time.ToString("yyyy-MM-dd HH:mm:ss"),
                ["correlationId"] = CorrelationId,
                ["conversationId"] = turn.Key.ConversationId,
                ["turnId"] = turn.Key.TurnId,
                ["attemptId"] = events.Select(item => item.AttemptId).LastOrDefault(id => id is not null),
                ["outcome"] = outcome,
                ["totalMs"] = Math.Round(events[^1].ElapsedMs),
                ["events"] = new JsonArray(events.Select(item => (JsonNode)new JsonObject
                {
                    ["atMs"] = Math.Round(item.ElapsedMs),
                    ["event"] = item.Name,
                    ["data"] = item.Data
                }).ToArray())
            };
            trace.Write($"{first.Time:yyyy-MM-dd HH:mm:ss} conversation={turn.Key.ConversationId} turn={turn.Key.TurnId} {outcome}", body);
        }
    }

    private string CorrelationId => Activity.Current?.TraceId.ToString() ?? _fallbackCorrelationId;

    private void Collect(string name, long conversationId, long? turnId, long? attemptId, object? data)
    {
        if (trace is not { Enabled: true }) return;
        lock (_events)
            _events.Add(new(name, conversationId, turnId, attemptId, DateTime.Now, Stopwatch.GetElapsedTime(_started).TotalMilliseconds, AgentTraceWriter.ToNode(data)));
    }

    private sealed record TraceEvent(string Name, long ConversationId, long? TurnId, long? AttemptId, DateTime Time, double ElapsedMs, JsonNode? Data);
}
