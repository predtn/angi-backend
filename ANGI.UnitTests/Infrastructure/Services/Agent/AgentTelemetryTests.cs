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
            var summary = File.ReadAllText(Directory.GetFiles(Path.Combine(root, "logs"), "agent-trace-*.log").Should().ContainSingle().Subject);
            var detail = File.ReadAllText(Directory.GetFiles(Path.Combine(root, "logs"), "agent-detail-*.log").Should().ContainSingle().Subject);
            summary.Should().Contain("conversation=7 turn=3 completed").And.Contain("đổi bữa tối ngày 2").And.Contain("VẤN ĐỀ: không có");
            detail.Should().Contain("conversation=7 turn=3 completed").And.Contain("\"user_message\"");
            logger.Messages.Should().NotContain(line => line.Contains("đổi bữa tối"));
        }
        finally { Directory.Delete(root, true); }
    }

    // TEST-05: A failed turn's summary lists the problems first, with the code line that recorded them and the exception's ANGI frames.
    /// <summary>Checks a reader can see what failed and where without opening the JSON.</summary>
    [Fact]
    public void Dispose_FailedTurn_SummaryPointsAtProblemAndCode()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            using (var telemetry = new AgentTelemetry(new CapturingLogger(), Writer(root, "Development")))
            {
                telemetry.Record("claimed", 7, 3, 4);
                telemetry.Record("llm", 7, 3, 4, new { elapsedMs = 1200.0, callNumber = 2, repair = false, outcome = "caller_cancelled" });
                telemetry.Record("turn_failed", 7, 3, 4, new { errorType = "TaskCanceledException" });
                telemetry.Trace("error", 7, 3, 4, new
                {
                    message = "The operation was canceled.",
                    detail = "System.Threading.Tasks.TaskCanceledException: The operation was canceled.\r\n ---> System.IO.IOException: Unable to read data\r\n"
                        + "   at ANGI.Infrastructure.Services.Agent.OpenAiAgentLlmService.SendAsync(AgentContext context) in E:\\x\\OpenAiAgentLlmService.cs:line 335\r\n"
                        + "   at ANGI.Application.Services.Agent.AgentOrchestrator.RunAsync(AgentContext context) in E:\\x\\AgentOrchestrator.cs:line 86"
                });
            }
            var summary = File.ReadAllText(Directory.GetFiles(Path.Combine(root, "logs"), "agent-trace-*.log").Single());
            summary.Should().Contain("✗ THẤT BẠI").And.Contain("VẤN ĐỀ (3 lỗi, 0 cảnh báo)")
                .And.Contain("caller_cancelled")
                .And.Contain("ghi nhận tại AgentTelemetryTests.Dispose_FailedTurn_SummaryPointsAtProblemAndCode")
                .And.Contain("nguyên nhân gốc: System.IO.IOException: Unable to read data")
                .And.Contain("ném ra tại OpenAiAgentLlmService.SendAsync (OpenAiAgentLlmService.cs:335)")
                .And.Contain("được gọi từ AgentOrchestrator.RunAsync (AgentOrchestrator.cs:86)");
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

    // TEST-03b: A very large event keeps its small fields and cuts only the large ones, so the summary still shows the reply (conversation 556).
    /// <summary>Checks that a long final_reply keeps "reply" readable.</summary>
    [Fact]
    public void ToNode_LargeEvent_KeepsSmallFields()
    {
        var node = AgentTraceWriter.ToNode(new { reply = "Mình thêm món nhé.", state = new string('x', 50_000) });

        node!["reply"]!.GetValue<string>().Should().Be("Mình thêm món nhé.");
        node["state"]!.GetValue<string>().Should().StartWith("[cắt bớt");
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
