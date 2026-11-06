namespace ANGI.Application.UseCases.Media.Upload;

/// <summary>
/// Checks the first bytes of an upload against its declared type (MEDIA-01). The browser's Content-Type is chosen by the
/// client, so without this a script or archive renamed to .pdf would be stored as a verification document.
/// </summary>
public static class MediaSignature
{
    /// <summary>Bytes read from the start of the file; enough for every signature below.</summary>
    public const int HeaderLength = 12;

    /// <summary>True when the header starts like a file of the given (normalized) MIME type.</summary>
    public static bool Matches(string mimeType, ReadOnlySpan<byte> header)
    {
        return mimeType switch
        {
            "image/jpeg" => header.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF }),
            "image/png" => header.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
            // RIFF <size> WEBP
            "image/webp" => header.Length >= 12 &&
                            header[..4].SequenceEqual("RIFF"u8) &&
                            header[8..12].SequenceEqual("WEBP"u8),
            "application/pdf" => header.StartsWith("%PDF"u8),
            _ => false
        };
    }

    /// <summary>
    /// Reads the header and returns a stream positioned at the start again: the same stream when it can seek, otherwise
    /// a buffered copy (uploads are at most 10 MB).
    /// </summary>
    public static async Task<(byte[] Header, Stream Content)> ReadHeaderAsync(Stream content, CancellationToken ct)
    {
        if (!content.CanSeek)
        {
            var buffered = new MemoryStream();
            await content.CopyToAsync(buffered, ct);
            buffered.Position = 0;
            content = buffered;
        }

        var start = content.Position;
        var header = new byte[HeaderLength];
        var read = 0;
        while (read < header.Length)
        {
            var count = await content.ReadAsync(header.AsMemory(read), ct);
            if (count == 0) break;
            read += count;
        }

        content.Position = start;
        return (header[..read], content);
    }
}
