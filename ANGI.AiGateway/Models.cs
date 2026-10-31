namespace ANGI.AiGateway;

public sealed class GatewayDenied(string code, int status = 503) : Exception(code)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}

public sealed record PreparedRequest(string Workload, byte[] Body, string InputHash, int MaxOutputTokens,
    decimal ReservedUsd, string PolicyHash);
public sealed record Reservation(Guid Id, string Workload, decimal ReservedUsd);
public sealed record CompletionUsage(int InputTokens, int OutputTokens, int? ReasoningTokens, decimal ChargedUpperUsd);
public sealed record BudgetChange(decimal DailyUsd, decimal MonthlyUsd, int DailyCalls, int MonthlyCalls);
public sealed record ControlChange(bool Stopped, string? ConfirmPricingVersion);
public sealed record GatewayResponse(int Status, byte[] Body, string? ErrorCode = null);

public interface IBudgetLedger
{
    Task<Reservation> ReserveAsync(PreparedRequest request, CancellationToken ct);
    Task SettleAsync(Reservation reservation, CompletionUsage? usage, string outcome, bool stop, CancellationToken ct);
}
