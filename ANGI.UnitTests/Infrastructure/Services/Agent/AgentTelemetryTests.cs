using System.Diagnostics;
using System.Text.RegularExpressions;
using ANGI.Infrastructure.Services.Agent;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace ANGI.UnitTests.Infrastructure.Services.Agent;

/// <summary>Verifies diagnostic correlation remains available even without an active HTTP trace.</summary>
public sealed class AgentTelemetryTests
{
    // TEST-01: Emit one stable nonempty correlation ID for all events in a scope that lacks an ambient Activity.
    /// <summary>Checks failure and tool diagnostics remain correlatable outside ordinary HTTP instrumentation.</summary>
    [Fact]
    public void Record_WithoutActivity_UsesStableCorrelation()
    {
        var previous = Activity.Current; Activity.Current = null;
        try
        {
            var logger = new CapturingLogger(); var telemetry = new AgentTelemetry(logger);
            telemetry.Record("claimed", 7, 1, 2);
            telemetry.Record("failed", 7, null, null, new { errorCode = "AGENT_UNAVAILABLE" });
            var correlations = logger.Messages.Select(line => Regex.Match(line, "correlationId=([a-f0-9]{32})").Groups[1].Value).ToArray();
            correlations.Should().OnlyContain(value => value.Length == 32); correlations.Distinct().Should().ContainSingle();
            logger.Messages.Last().Should().Contain("AGENT_UNAVAILABLE");
        }
        finally { Activity.Current = previous; }
    }

    /// <summary>Captures formatted diagnostic messages without writing external logs.</summary>
    private sealed class CapturingLogger : ILogger<AgentTelemetry>
    {
        public List<string> Messages { get; } = [];
        /// <summary>Needs no external scope resource for in-memory test logging.</summary>
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        /// <summary>Enables diagnostic capture at every level.</summary>
        public bool IsEnabled(LogLevel logLevel) => true;
        /// <summary>Captures the same formatted event the production logger would receive.</summary>
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
