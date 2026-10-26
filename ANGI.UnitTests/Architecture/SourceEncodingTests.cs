using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace ANGI.UnitTests.Architecture;

public sealed class SourceEncodingTests
{
    // UTF-8 Vietnamese read as Windows-1252 and saved again: "đã" becomes "Ä‘Ã£", "không" becomes "khÃ´ng".
    private static readonly Regex DoubleEncoded = new("Ã[\u0080-¿]|á»|áº|Ä‘|Æ°", RegexOptions.Compiled);

    // TEST-01: No source file carries double-encoded Vietnamese, which users would see in messages and notifications.
    [Fact]
    public void SourceFiles_ShouldNotContainDoubleEncodedVietnamese()
    {
        var root = FindSolutionRoot();
        var offenders = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"))
            .Where(path => !path.EndsWith(nameof(SourceEncodingTests) + ".cs", StringComparison.Ordinal))
            .SelectMany(path => File.ReadLines(path, Encoding.UTF8)
                .Select((line, index) => (Path.GetRelativePath(root, path), Line: index + 1, Text: line)))
            .Where(line => DoubleEncoded.IsMatch(line.Text))
            .Select(line => $"{line.Item1}:{line.Line}")
            .ToArray();

        offenders.Should().BeEmpty("re-save these files as UTF-8 and restore the Vietnamese text");
    }

    private static string FindSolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ANGI.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("ANGI.slnx was not found above the test output.");
    }
}
