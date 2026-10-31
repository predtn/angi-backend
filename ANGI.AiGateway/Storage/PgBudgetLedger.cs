using System.Data;
using System.Text.Json;
using Npgsql;

namespace ANGI.AiGateway.Storage;

/// <summary>Serializes admission in PostgreSQL across processes; crashes retain both funds and in-flight slots.</summary>
public sealed class PgBudgetLedger(GatewaySettings settings) : IBudgetLedger
{
    private static readonly TimeZoneInfo BudgetZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh");

    public async Task InitializeAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await ExecuteAsync(connection, transaction, "SELECT pg_advisory_xact_lock(7114103056)", ct);
        using var stream = typeof(PgBudgetLedger).Assembly.GetManifestResourceStream("ANGI.AiGateway.Storage.schema.sql")!;
        using var reader = new StreamReader(stream);
        await ExecuteAsync(connection, transaction, await reader.ReadToEndAsync(ct), ct);
        await ExecuteAsync(connection, transaction,
            "INSERT INTO ai_control.control(id,policy_hash) VALUES(1,@hash) ON CONFLICT DO NOTHING", ct, ("hash", settings.PolicyHash));
        await transaction.CommitAsync(ct);
    }

    public async Task<Reservation> ReserveAsync(PreparedRequest request, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var control = await LockAsync(connection, transaction, ct);
        if (control.Stopped || !control.PricingConfirmed) throw new GatewayDenied("gateway_spending_stopped");
        if (control.PolicyHash != settings.PolicyHash || request.PolicyHash != settings.PolicyHash)
            throw new GatewayDenied("gateway_policy_mismatch");
        var now = await NowAsync(connection, transaction, ct);
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, BudgetZone));
        if (day > settings.PricingValidUntil) throw new GatewayDenied("gateway_pricing_review_expired");
        var month = new DateOnly(day.Year, day.Month, 1);
        if (request.Workload != "chat" || request.ReservedUsd != RequestPolicy.Cost(GatewaySettings.ContextTokens, request.MaxOutputTokens) ||
            request.MaxOutputTokens < 1 || request.MaxOutputTokens > settings.ChatMaxOutputTokens ||
            request.Body.Length > settings.ChatMaxBodyBytes * 6 + 512)
            throw new GatewayDenied("gateway_invalid_reservation");

        var pending = await ScalarAsync<long>(connection, transaction,
            "SELECT count(*) FROM ai_control.reservations WHERE outcome='pending'", ct);
        if (pending >= settings.MaxInFlight) throw new GatewayDenied("gateway_concurrency_limit", 429);
        var recent = await ScalarAsync<long>(connection, transaction,
            "SELECT count(*) FROM ai_control.reservations WHERE admitted_at>@since", ct, ("since", now.AddMinutes(-1)));
        if (recent >= settings.RequestsPerMinute) throw new GatewayDenied("gateway_rate_limit", 429);
        foreach (var scope in new[] { "global", request.Workload })
        {
            var budget = await BudgetAsync(connection, transaction, scope, ct);
            await using var command = Command(connection, transaction, """
                SELECT count(*), count(*) FILTER(WHERE day_key=@day),
                    COALESCE(sum(COALESCE(charged_upper_usd,reserved_usd)),0),
                    COALESCE(sum(COALESCE(charged_upper_usd,reserved_usd)) FILTER(WHERE day_key=@day),0)
                FROM ai_control.reservations WHERE month_key=@month AND (@scope='global' OR workload=@scope)
                """, ("day", day), ("month", month), ("scope", scope));
            await using var reader = await command.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            if (reader.GetInt64(0) >= budget.MonthlyCalls || reader.GetInt64(1) >= budget.DailyCalls ||
                reader.GetDecimal(2) + request.ReservedUsd > budget.MonthlyUsd || reader.GetDecimal(3) + request.ReservedUsd > budget.DailyUsd)
                throw new GatewayDenied("gateway_budget_exhausted", 429);
        }

        var reservation = new Reservation(Guid.NewGuid(), request.Workload, request.ReservedUsd);
        await ExecuteAsync(connection, transaction, """
            INSERT INTO ai_control.reservations(id,workload,admitted_at,day_key,month_key,input_hash,reserved_usd)
            VALUES(@id,@workload,@now,@day,@month,@hash,@reserved)
            """, ct, ("id", reservation.Id), ("workload", request.Workload),
            ("now", now), ("day", day), ("month", month), ("hash", request.InputHash), ("reserved", request.ReservedUsd));
        await transaction.CommitAsync(ct);
        return reservation;
    }

    public async Task SettleAsync(Reservation reservation, CompletionUsage? usage, string outcome, bool stop, CancellationToken ct)
    {
        if (outcome is not ("success" or "unknown" or "upstream_error" or "abandoned")) throw new ArgumentException("Invalid outcome.");
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var control = await LockAsync(connection, transaction, ct);
        if (outcome == "abandoned" && !control.Stopped) throw new GatewayDenied("gateway_stop_before_abandoning", 409);
        await using var command = Command(connection, transaction,
            "SELECT reserved_usd,outcome FROM ai_control.reservations WHERE id=@id FOR UPDATE", ("id", reservation.Id));
        decimal reserved;
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) throw new GatewayDenied("gateway_reservation_not_found", 404);
            if (reader.GetString(1) != "pending") { await reader.DisposeAsync(); await transaction.CommitAsync(ct); return; }
            reserved = reader.GetDecimal(0);
        }
        if (usage is not null && (usage.ChargedUpperUsd < 0 || usage.ChargedUpperUsd > reserved || outcome != "success"))
            throw new GatewayDenied("gateway_invalid_settlement");
        await ExecuteAsync(connection, transaction, """
            UPDATE ai_control.reservations SET charged_upper_usd=@charged,input_tokens=@input,output_tokens=@output,
                reasoning_tokens=@reasoning,outcome=@outcome,settled_at=clock_timestamp() WHERE id=@id AND outcome='pending'
            """, ct, ("charged", usage?.ChargedUpperUsd ?? reserved), ("input", (object?)usage?.InputTokens ?? DBNull.Value),
            ("output", (object?)usage?.OutputTokens ?? DBNull.Value), ("reasoning", (object?)usage?.ReasoningTokens ?? DBNull.Value),
            ("outcome", outcome), ("id", reservation.Id));
        if (stop) await ExecuteAsync(connection, transaction, "UPDATE ai_control.control SET stopped=true WHERE id=1", ct);
        await transaction.CommitAsync(ct);
    }

    public async Task ChangeBudgetAsync(string workload, BudgetChange budget, CancellationToken ct)
    {
        if (workload is not ("global" or "chat") || budget.DailyUsd < 0 || budget.MonthlyUsd < budget.DailyUsd ||
            budget.DailyCalls < 0 || budget.MonthlyCalls < budget.DailyCalls || budget.MonthlyUsd > 1000 || budget.MonthlyCalls > 10000)
            throw new GatewayDenied("gateway_invalid_budget", 400);
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await LockAsync(connection, transaction, ct);
        // Changing limits never rewrites expenditure, request counters, or reservations.
        await ExecuteAsync(connection, transaction, """
            UPDATE ai_control.budgets SET daily_usd=@day,monthly_usd=@month,daily_calls=@dc,monthly_calls=@mc WHERE workload=@workload
            """, ct, ("day", budget.DailyUsd), ("month", budget.MonthlyUsd), ("dc", budget.DailyCalls),
            ("mc", budget.MonthlyCalls), ("workload", workload));
        await transaction.CommitAsync(ct);
    }

    public async Task ChangeControlAsync(ControlChange change, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var control = await LockAsync(connection, transaction, ct);
        if (!change.Stopped)
        {
            var now = await NowAsync(connection, transaction, ct);
            if (change.ConfirmPricingVersion != GatewaySettings.PriceVersion || DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, BudgetZone)) > settings.PricingValidUntil)
                throw new GatewayDenied("gateway_confirm_current_pricing", 409);
            if (control.PolicyHash != settings.PolicyHash)
            {
                if (!control.Stopped || await ScalarAsync<long>(connection, transaction,
                    "SELECT count(*) FROM ai_control.reservations WHERE outcome='pending'", ct) > 0)
                    throw new GatewayDenied("gateway_stop_and_settle_before_policy_change", 409);
            }
            await ExecuteAsync(connection, transaction,
                "UPDATE ai_control.control SET stopped=false,pricing_confirmed=true,policy_hash=@hash WHERE id=1", ct, ("hash", settings.PolicyHash));
        }
        else await ExecuteAsync(connection, transaction, "UPDATE ai_control.control SET stopped=true WHERE id=1", ct);
        await transaction.CommitAsync(ct);
    }

    public async Task<JsonElement> StatusAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await LockAsync(connection, transaction, ct);
        await using var command = Command(connection, transaction, """
            SELECT jsonb_build_object(
                'control',(SELECT to_jsonb(c) FROM ai_control.control c WHERE id=1),
                'budgets',(SELECT jsonb_agg(to_jsonb(b)) FROM ai_control.budgets b WHERE workload IN ('global','chat')),
                'usage',(SELECT COALESCE(jsonb_agg(to_jsonb(t)),'[]'::jsonb) FROM
                    (SELECT workload,day_key,month_key,count(*) AS calls,
                        sum(COALESCE(charged_upper_usd,reserved_usd)) AS committed_upper_usd,
                        sum(input_tokens) AS input_tokens,sum(output_tokens) AS output_tokens,
                        sum(reasoning_tokens) AS reasoning_tokens FROM ai_control.reservations
                        GROUP BY workload,day_key,month_key) t),
                'reservations',(SELECT COALESCE(jsonb_agg(to_jsonb(r)),'[]'::jsonb) FROM
                    (SELECT id,workload,admitted_at,input_hash,reserved_usd,charged_upper_usd,input_tokens,
                        output_tokens,reasoning_tokens,outcome FROM ai_control.reservations ORDER BY admitted_at DESC LIMIT 100) r)
            )::text
            """);
        var json = (string)(await command.ExecuteScalarAsync(ct))!;
        await transaction.CommitAsync(ct);
        return JsonSerializer.Deserialize<JsonElement>(json);
    }

    public async Task EnsureReadyAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM ai_control.control WHERE id=1", connection);
        if ((long)(await command.ExecuteScalarAsync(ct))! != 1) throw new GatewayDenied("gateway_ledger_not_initialized");
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.ConnectionString)) throw new GatewayDenied("gateway_ledger_unavailable");
        var builder = new NpgsqlConnectionStringBuilder(settings.ConnectionString) { Timeout = 3, CommandTimeout = 5 };
        var connection = new NpgsqlConnection(builder.ConnectionString);
        try { await connection.OpenAsync(ct); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }

    private static async Task<(bool Stopped, bool PricingConfirmed, string PolicyHash)> LockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, "SELECT stopped,pricing_confirmed,policy_hash FROM ai_control.control WHERE id=1 FOR UPDATE");
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new GatewayDenied("gateway_ledger_not_initialized");
        return (reader.GetBoolean(0), reader.GetBoolean(1), reader.GetString(2));
    }

    private static async Task<BudgetChange> BudgetAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string scope, CancellationToken ct)
    {
        await using var command = Command(connection, transaction,
            "SELECT daily_usd,monthly_usd,daily_calls,monthly_calls FROM ai_control.budgets WHERE workload=@scope", ("scope", scope));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new GatewayDenied("gateway_budget_not_initialized");
        return new(reader.GetDecimal(0), reader.GetDecimal(1), reader.GetInt32(2), reader.GetInt32(3));
    }

    private static Task<DateTime> NowAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct) =>
        ScalarAsync<DateTime>(connection, transaction, "SELECT clock_timestamp()", ct);

    private static NpgsqlCommand Command(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, params (string Name, object Value)[] parameters)
    {
        var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return command;
    }

    private static async Task<int> ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var command = Command(connection, transaction, sql, parameters);
        return await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var command = Command(connection, transaction, sql, parameters);
        return (T)(await command.ExecuteScalarAsync(ct))!;
    }
}
