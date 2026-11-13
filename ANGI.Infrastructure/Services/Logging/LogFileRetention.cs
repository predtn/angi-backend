using System.Globalization;

namespace ANGI.Infrastructure.Services.Logging;

/// <summary>Deletes dated local log files ("prefix-yyyyMMdd.log") older than the retention window.</summary>
public static class LogFileRetention
{
    /// <summary>Removes files of one prefix whose date in the name is older than <paramref name="days"/> days; never throws.</summary>
    public static void Purge(string directory, string prefix, int days, DateTime today)
    {
        try
        {
            if (!Directory.Exists(directory)) return;
            var oldest = today.Date.AddDays(-(days - 1));
            foreach (var path in Directory.EnumerateFiles(directory, $"{prefix}-*.log"))
            {
                var stamp = Path.GetFileNameWithoutExtension(path)[(prefix.Length + 1)..];
                if (DateTime.TryParseExact(stamp, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) && day < oldest)
                {
                    try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Path of today's file for one prefix.</summary>
    public static string PathFor(string directory, string prefix, DateTime day) =>
        Path.Combine(directory, $"{prefix}-{day:yyyyMMdd}.log");
}
