using System.Security.Cryptography;
using System.Text;

namespace ANGI.AiGateway;

/// <summary>Spending boundary for backend AI clients; only this process receives their provider credential.</summary>
public sealed class GatewaySettings
{
    public string ConnectionString { get; set; } = "";
    public string ProviderApiKey { get; set; } = "";
    public string ChatKey { get; set; } = "";
    public string AdminKey { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 30;
    public int MaxInFlight { get; set; } = 1;
    public int RequestsPerMinute { get; set; } = 2;
    public int ChatMaxOutputTokens { get; set; } = 16000;
    public int ChatMaxBodyBytes { get; set; } = 196608;

    public const string Model = "gpt-6-luna";
    public const int ContextTokens = 1_050_000;
    // Approved worst-case ceilings include long-context and cache-write premiums, without cache discounts.
    public const decimal InputUsdPerMillion = 0.25m;
    public const decimal OutputUsdPerMillion = 0.75m;
    public const string PriceVersion = "gpt-6-luna:2026-10-31:input-ceiling=0.25:output-ceiling=0.75";
    public DateOnly PricingValidUntil { get; set; } = new(2026, 11, 6);

    public string PolicyHash => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        $"backend-chat|{PriceVersion}|{PricingValidUntil:yyyy-MM-dd}|{TimeoutSeconds}|{MaxInFlight}|{RequestsPerMinute}|{ChatMaxOutputTokens}|{ChatMaxBodyBytes}")));

    public void Validate()
    {
        if (TimeoutSeconds is < 1 or > 120 || MaxInFlight is < 1 or > 10 || RequestsPerMinute is < 1 or > 60 ||
            ChatMaxOutputTokens is < 1 or > 16000 || ChatMaxBodyBytes is < 1024 or > 262144)
            throw new InvalidOperationException("Invalid AI gateway resource limits.");
        var keys = new[] { ChatKey, AdminKey };
        if (keys.Where(key => key.Length > 0).Any(key => key.Length < 40 || key.Any(char.IsWhiteSpace) || key.Any(char.IsControl)) ||
            keys.Where(key => key.Length > 0).Distinct(StringComparer.Ordinal).Count() != keys.Count(key => key.Length > 0))
            throw new InvalidOperationException("Gateway credentials must be distinct secrets of at least 40 characters.");
        if (ChatKey.Length > 0 && !ChatKey.StartsWith("angi-chat-", StringComparison.Ordinal) ||
            AdminKey.Length > 0 && !AdminKey.StartsWith("angi-admin-", StringComparison.Ordinal))
            throw new InvalidOperationException("Use workload-scoped gateway credentials.");
    }

    public string? Authenticate(string? authorization)
    {
        if (authorization is null || !authorization.StartsWith("Bearer ", StringComparison.Ordinal)) return null;
        var token = authorization[7..];
        foreach (var (key, scope) in new[] { (ChatKey, "chat"), (AdminKey, "admin") })
            if (key.Length >= 40 && CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(token)),
                    SHA256.HashData(Encoding.UTF8.GetBytes(key)))) return scope;
        return null;
    }
}
