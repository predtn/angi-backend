using System.Buffers.Text;
using System.Text.Json;

namespace ANGI.Application.UseCases.Discovery;

/// <summary>
/// Opaque DISH-01 cursor: base64url of {"k": list key, "o": offset}. The app only sends it back; the list itself stays in
/// the 30-minute cache.
/// </summary>
internal static class DishListCursor
{
    private sealed record Payload(Guid K, int O);

    public static string Encode(Guid key, int offset) =>
        Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(new Payload(key, offset)));

    /// <summary>False for anything that is not a cursor this class wrote.</summary>
    public static bool TryDecode(string cursor, out Guid key, out int offset)
    {
        key = Guid.Empty;
        offset = 0;
        try
        {
            var payload = JsonSerializer.Deserialize<Payload>(Base64Url.DecodeFromChars(cursor));
            if (payload is null || payload.K == Guid.Empty || payload.O < 0)
            {
                return false;
            }

            (key, offset) = (payload.K, payload.O);
            return true;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            return false;
        }
    }
}
