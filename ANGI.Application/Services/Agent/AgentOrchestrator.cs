using System.Diagnostics;
using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Services.Agent;
using ANGI.Application.Common.Models.Agent;

namespace ANGI.Application.Services.Agent;

/// <summary>Runs bounded LLM/tool rounds outside a database transaction and prepares validated immutable output.</summary>
public sealed class AgentOrchestrator(IAgentLlmService llm, AgentToolExecutor tools, AgentStatePolicy statePolicy,
    AgentProposalBuilder builder, AgentProposalValidator validator, AgentLimits limits, IAgentTelemetry telemetry)
{
    /// <summary>Enforces one schema repair, total calls, bounded context and a backend-controlled timeout.</summary>
    public async Task<AgentPreparedTurn> RunAsync(AgentContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (AgentTasteClarificationPolicy.Question(context) is { } tasteQuestion)
        {
            telemetry.Record("taste_clarification", context.ConversationId, context.TurnId, context.AttemptId);
            return new(tasteQuestion, context.State with { PendingQuestion = tasteQuestion }, null, null);
        }
        // Timeout áp dụng cho toàn bộ vòng LLM/tools của lượt chat; ct vẫn giữ quyền hủy từ caller.
        // Use case đã commit bước claim trước khi vào đây nên không giữ transaction DB trong lúc chờ AI.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(limits.TimeoutSeconds));
        var toolRounds = 0;
        var toolCalls = 0;
        var repaired = false;
        var transientRetries = 0;
        var turnStarted = Stopwatch.GetTimestamp();
        var confirmedState = context.ConstraintBaseline = context.State;
        try
        {
            for (var calls = 0; calls < limits.MaxLlmCalls; calls++)
            {
                // Mỗi vòng có thể là một lần model xin tools hoặc trả output cuối; đều tính vào MaxLlmCalls.
                timeout.Token.ThrowIfCancellationRequested();
                if (AgentJson.Write(new { context.State, context.Source, context.History, context.Request, context.Exchanges }).Length > limits.MaxContextCharacters)
                    throw Unavailable();
                var started = Stopwatch.GetTimestamp();
                // ĐÂY LÀ DÒNG GỌI LLM qua IAgentLlmService (DI nối tới OpenAiAgentLlmService).
                // Adapter dựng system prompt + context + strict schema rồi gửi HTTP tới provider.
                // API key được gắn vào Authorization header trong adapter, không nằm trong AgentContext.
                AgentLlmStep? step;
                try { step = await llm.RunAsync(context, repaired, timeout.Token); }
                catch (OperationCanceledException)
                {
                    telemetry.Record("llm", context.ConversationId, context.TurnId, context.AttemptId, new
                    {
                        elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds, callNumber = calls + 1,
                        repair = repaired, outcome = ct.IsCancellationRequested ? "caller_cancelled" : "turn_timeout"
                    });
                    throw;
                }
                if (step is null || step.Failure is null && (step.Tools is null || step.Tools.Length == 0 && step.Output is null || step.Tools.Length > 0 && step.Output is not null))
                    step = AgentLlmStep.Failed(AgentLlmFailureKind.InvalidResponse);
                telemetry.Record("llm", context.ConversationId, context.TurnId, context.AttemptId, new
                {
                    elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds, step.Tokens, callNumber = calls + 1,
                    repair = repaired, failureKind = step.Failure?.Kind.ToString(), step.Failure?.HttpStatus, step.Failure?.RetryAfterSeconds,
                    step.Failure?.ProviderCode, step.Failure?.Reason
                });
                // Repair model output once; retry only classified temporary provider failures within the same deadline.
                if (step.Failure is { } failure)
                {
                    if (failure.Kind == AgentLlmFailureKind.InvalidSchema && !repaired) { repaired = true; continue; }
                    if (failure.Kind == AgentLlmFailureKind.RateLimited && failure.Reason == "rate_limit" &&
                        failure.RetryAfterSeconds is > 0 and <= 45 && transientRetries < 2 && calls + 1 < limits.MaxLlmCalls &&
                        Stopwatch.GetElapsedTime(turnStarted).TotalSeconds + failure.RetryAfterSeconds.Value + 20 < limits.TimeoutSeconds)
                    {
                        transientRetries++;
                        telemetry.Record("llm_retry", context.ConversationId, context.TurnId, context.AttemptId,
                            new { retry = transientRetries, delaySeconds = failure.RetryAfterSeconds.Value, failure.HttpStatus, failure.ProviderCode, failure.Reason });
                        await Task.Delay(TimeSpan.FromSeconds(failure.RetryAfterSeconds.Value), timeout.Token);
                        continue;
                    }
                    if (failure.Kind == AgentLlmFailureKind.ProviderUnavailable && failure.HttpStatus is 500 or 502 or 503 &&
                        transientRetries < 2 && calls + 1 < limits.MaxLlmCalls && failure.RetryAfterSeconds is not > 10)
                    {
                        var delay = failure.RetryAfterSeconds ?? (1 << transientRetries);
                        transientRetries++;
                        telemetry.Record("llm_retry", context.ConversationId, context.TurnId, context.AttemptId,
                            new { retry = transientRetries, delaySeconds = delay, failure.HttpStatus, failure.ProviderCode, failure.Reason });
                        await Task.Delay(TimeSpan.FromSeconds(delay), timeout.Token);
                        continue;
                    }
                    throw new AgentProviderException(failure);
                }
                if (step.Tools.Length > 0)
                {
                    // Tool call không phải action cuối: model đang xin dữ liệu để tiếp tục suy luận.
                    // Backend giới hạn số vòng/số calls và yêu cầu call ID rõ ràng trước khi chạy tools.
                    if (++toolRounds > limits.MaxToolRounds || (toolCalls += step.Tools.Length) > limits.MaxToolCalls ||
                        step.Tools.Any(x => x is null || string.IsNullOrWhiteSpace(x.Id)) || step.Tools.Select(x => x.Id).Distinct().Count() != step.Tools.Length)
                        throw Unavailable();
                    var results = new System.Text.Json.JsonElement[step.Tools.Length];
                    for (var index = 0; index < step.Tools.Length; index++) results[index] = await tools.ExecuteAsync(context, step.Tools[index], timeout.Token);
                    // Giữ liên kết call ID -> result để lần gọi LLM kế tiếp nhận đúng dữ liệu vừa lấy.
                    context.Exchanges.Add(new(step.Tools, results));
                    continue;
                }
                var output = step.Output!;
                // Action do LLM chọn theo Instructions trong adapter; hợp đồng chỉ cho phép các action
                // được khai báo ở AgentLlmSchema.Output, đồng thời kiểm tra lại allowlist tại đây.
                // Đây là quy tắc xử lý của ANGI, không có điểm số confidence/threshold phân loại intent riêng.
                if (string.IsNullOrWhiteSpace(output.Reply) || output.Reply.Length > limits.MaxMessageLength ||
                    output.Clarification is not null && !AgentReplyBuilder.Clarifications.Contains(output.Clarification) ||
                    output.Items is null || output.Changes is null || output.Scope is null ||
                    output.Action is not ("clarify" or "answer" or "create_proposal" or "patch_proposal" or "discard_proposal" or "reset_conversation"))
                {
                    if (repaired) throw Unavailable();
                    repaired = true;
                    continue;
                }
                // Model có thể bổ sung/siết điều kiện, nhưng không được tự nới ràng buộc đã chốt.
                // Cờ ConstraintsChangeConfirmed không cấp quyền; backend giữ pending change để người dùng xác nhận.
                if (output.Action is "discard_proposal" or "reset_conversation")
                {
                    if (output.Items.Length != 0 || output.Changes.Length != 0 || output.State is not null) throw Unavailable();
                    return new(output.Action == "discard_proposal" ? "Đã bỏ bản đề xuất đang chỉnh sửa. Lịch đã lưu được giữ nguyên."
                        : "Đã bắt đầu lại ngữ cảnh lập lịch. Lịch đã lưu được giữ nguyên.", context.State, null, null,
                        output.Action == "discard_proposal" ? "discard" : "reset");
                }
                if (!context.ReplanAfterConfirmation)
                    context.State = statePolicy.Accept(context.State, output.State, confirmedState);
                if (output.Action is "clarify" or "answer")
                {
                    // clarify: thiếu tỉnh/ngày/ngân sách, chưa rõ món hoặc nội dung cần sửa thì hỏi lại.
                    // answer: giải thích bằng ngữ cảnh sẵn có. Cả hai không được kèm items/changes để sửa lịch.
                    // Với prompt "sửa roadmap cho tôi" mà chưa rõ sửa gì, Instructions hướng dẫn clarify;
                    // code ở đây kiểm tra hợp đồng output, không tự hiểu/chấm đúng sai ngữ nghĩa câu hỏi.
                    if (output.Items.Length != 0 || output.Changes.Length != 0) throw Unavailable();
                    if (output.Action == "answer" && context.Source is { } current && current.Items.Any(item =>
                        AgentScopedPlanning.RequiresRefresh(current.State, context.State, item)))
                    {
                        if (!repaired)
                        {
                            context.RepairFeedback = "Yêu cầu ngày/bữa đã thay đổi. Cần lấy gợi ý mới cho các bữa bị ảnh hưởng và tạo patch_proposal; không chỉ trả answer.";
                            repaired = true;
                            continue;
                        }
                        const string pending = "Yêu cầu đã thay đổi nhưng chưa có đề xuất cập nhật hợp lệ. Cần lấy lại gợi ý cho các ngày/bữa bị ảnh hưởng trước khi lưu.";
                        return new(pending, context.State with { PendingQuestion = pending }, null, null);
                    }
                    if (output.Action == "answer" && context.State.PreferenceApplication == "future" && context.Source is { } futureSource &&
                        futureSource.Items.Any(item => AgentScopedPlanning.Fingerprint(futureSource.State, item.DayNumber, item.MealSlot) !=
                            AgentScopedPlanning.Fingerprint(context.State, item.DayNumber, item.MealSlot)))
                    {
                        var future = futureSource with { State = context.State with { PendingQuestion = null }, Changes = [],
                            RetainedItems = futureSource.Items, Scope = new() };
                        var checkedFuture = await validator.ValidateAsync(future, futureSource, false, true, timeout.Token, context.Grants);
                        if (checkedFuture.Validation.Errors.Length > 0)
                            return new("Chưa thể giữ nguyên đề xuất theo các điều kiện mới. Vui lòng kiểm tra lại yêu cầu.", context.State, null, checkedFuture.Validation);
                        return new("Đã cập nhật khẩu vị cho các gợi ý tiếp theo. Các món trong bản đề xuất hiện tại được giữ nguyên; lịch chính thức chưa thay đổi.",
                            future.State, checkedFuture.Snapshot, checkedFuture.Validation);
                    }
                    var reply = output.Action == "clarify" ? AgentReplyBuilder.Clarify(context, output.Clarification) : AgentReplyBuilder.Answer(context);
                    if (output.Action == "clarify") context.State = context.State with
                        { PendingQuestion = reply[..Math.Min(reply.Length, 1000)] };
                    return new(reply, context.State, null, null);
                }
                // create_proposal: lịch mới; patch_proposal: add/replace/remove/move trên lịch nguồn.
                // Builder kiểm tra nguồn, scope và baseProposalVersion; Validator kiểm tra catalog,
                // nguồn candidate, giá, ngân sách và tuyến. Action hợp lệ chưa có nghĩa proposal hợp lệ.
                context.State = context.State with { PendingQuestion = null };
                telemetry.Record("proposal_output", context.ConversationId, context.TurnId, context.AttemptId,
                    new { output.Action, output.Scope, output.BaseProposalVersion, itemCount = output.Items.Length,
                        changeCount = output.Changes.Length, sourceItemCount = context.Source?.Items.Length });
                AgentSnapshot snapshot;
                try { snapshot = builder.Build(context, output); }
                catch (ConflictException error) when (error.ErrorCode == "PROPOSAL_NOT_COMMITTABLE" && !repaired)
                {
                    telemetry.Record("proposal_repair", context.ConversationId, context.TurnId, context.AttemptId,
                        new { output.Action, output.Scope, error.ErrorCode, reason = error.Message });
                    context.RepairFeedback = error.Message;
                    repaired = true;
                    continue;
                }
                var validated = await validator.ValidateAsync(snapshot, context.Source, false, true, timeout.Token, context.Grants);
                telemetry.Record("validation", context.ConversationId, context.TurnId, context.AttemptId, new { validated.Validation.Errors, validated.Validation.Warnings });
                // Có lỗi thì không trả snapshot mới để lưu Ready; proposal trước được giữ lại.
                if (validated.Validation.Errors.Length > 0) return new("Đề xuất chưa hợp lệ. Vui lòng làm rõ hoặc điều chỉnh các điều kiện.", context.State, null, validated.Validation);
                // Mới là kết quả chuẩn bị. SendAgentMessageUseCase còn kiểm tra lease rồi mới lưu proposal;
                // chỉ writer chung của confirm/chat-save mới áp dụng proposal sau xác nhận rõ ràng.
                var reviewedState = context.State with { FocusDay = null, FocusMeal = null };
                return new(AgentReplyBuilder.Proposal(validated.Snapshot, validated.Validation), reviewedState,
                    validated.Snapshot with { State = reviewedState }, validated.Validation);
            }
            // Đã dùng hết số lần gọi cho phép mà chưa có kết quả cuối hợp lệ.
            throw Unavailable();
        }
        catch (AgentStateChangeRequiredException change) { return AgentChatPolicy.StageChange(confirmedState, change.Proposed); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new AgentProviderException(new(AgentLlmFailureKind.TurnTimeout)); }
        catch (FluentValidation.ValidationException) { throw Unavailable(); }
    }

    /// <summary>Creates a controlled provider/limit error without replacing the last ready proposal.</summary>
    private static ServiceUnavailableException Unavailable() => new("AGENT_UNAVAILABLE", "Agent chưa hoàn tất yêu cầu. Vui lòng thử lại.");
}

/// <summary>Prepared turn data that must pass lease fencing before persistence.</summary>
public sealed record AgentPreparedTurn(string Reply, AgentState State, AgentSnapshot? Snapshot, AgentValidation? Validation,
    string? Action = null, AgentCommitPreparation? Commit = null);
