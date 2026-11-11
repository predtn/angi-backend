using System.Diagnostics;
using System.Text.RegularExpressions;
using ANGI.Infrastructure.Services.Agent;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
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
        // STEP-01: Derive previous from Activity.Current, set Activity.Current to null, create logger as CapturingLogger, create telemetry as AgentTelemetry and 2 more.
        var previous = Activity.Current; Activity.Current = null;
        try
        {
            var logger = new CapturingLogger(); var telemetry = new AgentTelemetry(logger);
            telemetry.Record("claimed", 7, 1, 2);
            telemetry.Record("failed", 7, null, null, new { errorCode = "AGENT_UNAVAILABLE" });
            var correlations = logger.Messages.Select(line => Regex.Match(line, "correlationId=([a-f0-9]{32})").Groups[1].Value).ToArray();
            // STEP-02: Assert correlations only holds items matching the condition, correlations.Distinct() has exactly one matching item and 1 more.
            correlations.Should().OnlyContain(value => value.Length == 32); correlations.Distinct().Should().ContainSingle();
            logger.Messages.Last().Should().Contain("AGENT_UNAVAILABLE");
        }
        // STEP-03: Clean up: set Activity.Current to previous.
        finally { Activity.Current = previous; }
    }

    // TEST-02: In Development with the trace on, one finished turn is written with its content, and the content never reaches the normal log.
    /// <summary>Checks the local trace file holds the user text and reply, grouped under the turn header.</summary>
    [Fact]
    public void Dispose_InDevelopment_WritesTurnTraceOnly()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var logger = new CapturingLogger();
            using (var telemetry = new AgentTelemetry(logger, Writer(root, "Development")))
            {
                telemetry.Record("claimed", 7, 3, 4);
                telemetry.Trace("user_message", 7, 3, 4, new { content = "đổi bữa tối ngày 2" });
                telemetry.Record("completed", 7, 3, 4);
            }
            var file = Directory.GetFiles(Path.Combine(root, "logs"), "agent-trace-*.log").Should().ContainSingle().Subject;
            var text = File.ReadAllText(file);
            text.Should().Contain("conversation=7 turn=3 completed").And.Contain("đổi bữa tối ngày 2").And.Contain("\"user_message\"");
            logger.Messages.Should().NotContain(line => line.Contains("đổi bữa tối"));
        }
        finally { Directory.Delete(root, true); }
    }

    // TEST-03: Outside Development nothing is written even when the setting is on.
    /// <summary>Checks a server never stores chat content.</summary>
    [Fact]
    public void Dispose_InProduction_WritesNothing()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            using (var telemetry = new AgentTelemetry(new CapturingLogger(), Writer(root, "Production")))
            {
                telemetry.Trace("user_message", 7, 3, 4, new { content = "xin chào" });
                telemetry.Record("completed", 7, 3, 4);
            }
            Directory.Exists(Path.Combine(root, "logs")).Should().BeFalse();
        }
        finally { Directory.Delete(root, true); }
    }

    // TEST-04: Files older than the retention window are deleted; recent ones and other files stay.
    /// <summary>Checks the 7-day cleanup.</summary>
    [Fact]
    public void Purge_RemovesOnlyFilesOlderThanRetention()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var today = new DateTime(2026, 11, 11);
            foreach (var name in new[] { "agent-trace-20261104.log", "agent-trace-20261105.log", "agent-trace-20261111.log", "app-20261101.log" })
                File.WriteAllText(Path.Combine(root, name), "x");
            ANGI.Infrastructure.Services.Logging.LogFileRetention.Purge(root, "agent-trace", 7, today);
            Directory.GetFiles(root).Select(Path.GetFileName).Should().BeEquivalentTo("agent-trace-20261105.log", "agent-trace-20261111.log", "app-20261101.log");
        }
        finally { Directory.Delete(root, true); }
    }

    private static AgentTraceWriter Writer(string root, string environment) =>
        new(new FakeEnvironment(environment, root), new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AgentTrace:Enabled"] = "true" }).Build());

    private sealed class FakeEnvironment(string name, string root) : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "ANGI.WebApi";
        public string ContentRootPath { get; set; } = root;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
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
