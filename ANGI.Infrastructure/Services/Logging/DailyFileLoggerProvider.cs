using Microsoft.Extensions.Logging;

namespace ANGI.Infrastructure.Services.Logging;

/// <summary>Development-only logger writing one file per day ("app-yyyyMMdd.log") and keeping the last few days.</summary>
/// <remarks>Uses the same level filters as the console ("Logging:LogLevel"). Files stay on this machine; "logs/" is git-ignored.</remarks>
[ProviderAlias("File")]
public sealed class DailyFileLoggerProvider(string directory, int retentionDays = 7, string prefix = "app") : ILoggerProvider
{
    private readonly object _gate = new();
    private DateTime _day;
    private StreamWriter? _writer;

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
        lock (_gate) { _writer?.Dispose(); _writer = null; }
    }

    private void Write(string line)
    {
        lock (_gate)
        {
            try
            {
                var now = DateTime.Now;
                if (_writer is null || now.Date != _day)
                {
                    // Sang ngày mới: mở file mới và xóa file quá hạn giữ lại.
                    _writer?.Dispose();
                    Directory.CreateDirectory(directory);
                    var stream = new FileStream(LogFileRetention.PathFor(directory, prefix, now), FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    _writer = new StreamWriter(stream) { AutoFlush = true };
                    _day = now.Date;
                    LogFileRetention.Purge(directory, prefix, retentionDays, now);
                }
                _writer.WriteLine(line);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed class FileLogger(DailyFileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{logLevel}] {category}: {formatter(state, exception)}";
            if (exception is not null) line += Environment.NewLine + exception;
            owner.Write(line);
        }
    }
}
