using System.Diagnostics;
using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Services.Agent;
using ANGI.Application.Common.Models.Agent;

namespace ANGI.Application.Services.Agent;

/// <summary>Runs bounded LLM/tool rounds outside a database transaction and prepares validated immutable output.</summary>
public sealed class AgentOrchestrator(IAgentLlmService llm, AgentToolExecutor tools, AgentStatePolicy statePolicy,
    AgentProposalBuilder builder, AgentProposalValidator validator, AgentLimits limits, IAgentTelemetry telemetry)
{
    /// <summary>Bounds schema, proposal and unsupported-coverage repairs within the same call and timeout limits.</summary>
    public async Task<AgentPreparedTurn> RunAsync(AgentContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (AgentTasteClarificationPolicy.Question(context) is { } tasteQuestion)
        {
            var pending = AgentPendingPlanningPolicy.Capture(context.State.PendingPlanningRequest, context.Request.Content);
            // Hỏi gộp số ngày/ngân sách còn thiếu trong cùng câu, để lượt sau không gọi gợi ý khi chưa có ngân sách.
            if (AgentPendingPlanningPolicy.MissingTripDetails(context.State, pending) is { } tripQuestion) tasteQuestion += " " + tripQuestion;
            telemetry.Record("taste_clarification", context.ConversationId, context.TurnId, context.AttemptId);
            return new(tasteQuestion, context.State with { PendingQuestion = tasteQuestion, PendingPlanningRequest = pending }, null, null);
        }
        // Câu trả lời khẩu vị backend đã đọc đủ (vị, mức, phạm vi): dùng làm phản hồi nếu model vẫn hỏi lại.
        var tasteResolution = AgentTasteClarificationPolicy.Resolution(context);
        // Người dùng bỏ qua câu hỏi khẩu vị: xóa nó để lượt sau không bị đọc lại như câu trả lời khẩu vị.
        // Câu trả lời thật thì giữ câu hỏi trong state để model hiểu "Ít, toàn chuyến đi" đang trả lời câu nào.
        if (AgentTasteClarificationPolicy.IsIgnored(context)) context.State = context.State with { PendingQuestion = null };
        if (context.State.PendingPlanningRequest is { } pendingRequest)
            context.State = context.State with { PendingPlanningRequest = AgentPendingPlanningPolicy.Capture(pendingRequest, context.Request.Content) };
        // "Bữa 5" khi mỗi ngày chỉ có sáng/trưa/tối: hỏi lại thay vì để model đoán bữa.
        if (AgentMealScopePolicy.Question(context.Request.Content, context.State) is { } mealQuestion)
        {
            telemetry.Record("meal_clarification", context.ConversationId, context.TurnId, context.AttemptId);
            return new(mealQuestion, context.State with { PendingQuestion = mealQuestion }, null, null);
        }
        // "Đổi cái đó đi" khi đã có lịch: hỏi đổi món nào, không để model tự đoán thành đổi cả lịch.
        if (AgentEditScopePolicy.Question(context) is { } editQuestion)
        {
            telemetry.Record("edit_scope_clarification", context.ConversationId, context.TurnId, context.AttemptId);
            return new(editQuestion, context.State with { PendingQuestion = editQuestion }, null, null);
        }
        // "Bỏ bữa sáng" khi lịch nhiều ngày: hỏi ngày nào thay vì để model đọc thành đổi một món.
        if (AgentRemovalPolicy.Question(context) is { } removalQuestion)
        {
            telemetry.Record("removal_clarification", context.ConversationId, context.TurnId, context.AttemptId);
            return new(removalQuestion, context.State with { PendingQuestion = removalQuestion }, null, null);
        }
        // "Xóa bữa tối ngày 2", "bỏ món Phở Gà": đã biết chính xác món cần bỏ, không cần LLM và không bị luật đủ bữa từ chối.
        if (AgentRemovalPolicy.Targets(context) is { } removedItems)
        {
            telemetry.Record("removal", context.ConversationId, context.TurnId, context.AttemptId, new { count = removedItems.Length });
            return await RemoveAsync(context, removedItems, ct);
        }
        // "ok", "thôi" khi không có câu hỏi nào đang mở: không có gì để lập lịch, trả lời cố định thay vì tốn 2-3 lần gọi LLM rồi bị loại.
        if (AgentEditScopePolicy.IsBareAcknowledgement(context))
        {
            telemetry.Record("acknowledgement", context.ConversationId, context.TurnId, context.AttemptId);
            return new(AgentReplyBuilder.Clarify(context, "general") + " Nhắn ‘chốt lịch này’ nếu bạn muốn lưu bản hiện tại.", context.State, null, null);
        }
        // "Món lạ/món mới/món chưa ăn" không có tag nào tương ứng: hỏi nghĩa trước, không để model tra nó như tên món.
        if (AgentNoveltyPolicy.Question(context) is { } noveltyQuestion)
        {
            telemetry.Record("novelty_clarification", context.ConversationId, context.TurnId, context.AttemptId);
            return new(noveltyQuestion, context.State with { PendingQuestion = noveltyQuestion }, null, null);
        }
        // Timeout áp dụng cho toàn bộ vòng LLM/tools của lượt chat; ct vẫn giữ quyền hủy từ caller.
        // Use case đã commit bước claim trước khi vào đây nên không giữ transaction DB trong lúc chờ AI.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(limits.TimeoutSeconds));
        var toolRounds = 0;
        var toolCalls = 0;
        var repaired = false;
        var schemaRepaired = false;
        var proposalRepaired = false;
        // Đề xuất rỗng có lượt sửa riêng để không tiêu mất lượt sửa lỗi validation của lịch đủ món sau đó.
        var emptyRepaired = false;
        var coverageRepaired = false;
        var budgetRepaired = false;
        var tasteRepaired = false;
        AgentValidation? lastValidation = null;
        var transientRetries = 0;
        var executedTools = new HashSet<string>(StringComparer.Ordinal);
        var turnStarted = Stopwatch.GetTimestamp();
        var confirmedState = context.ConstraintBaseline = context.State;
        try
        {
            for (var calls = 0; calls < limits.MaxLlmCalls; calls++)
            {
                // Mỗi vòng có thể là một lần model xin tools hoặc trả output cuối; đều tính vào MaxLlmCalls.
                timeout.Token.ThrowIfCancellationRequested();
                if (AgentJson.Write(new { context.State, context.Source, context.History, context.Request, context.Exchanges, context.RejectedOutput }).Length > limits.MaxContextCharacters)
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
                    repair = repaired, limits.MaxOutputTokens, limits.ReasoningEffort,
                    schemaRepaired, proposalRepaired, emptyRepaired, coverageRepaired,
                    failureKind = step.Failure?.Kind.ToString(), step.Failure?.HttpStatus, step.Failure?.RetryAfterSeconds,
                    step.Failure?.ProviderCode, step.Failure?.Reason
                });
                // Repair a malformed schema once; planning repairs have independent bounded attempts under the same deadline.
                if (step.Failure is { } failure)
                {
                    if (failure.Kind == AgentLlmFailureKind.InvalidSchema && !schemaRepaired)
                    {
                        // Báo đúng đường dẫn trường sai để lần sửa duy nhất có cơ hội thành công thay vì đoán lại toàn bộ.
                        if (failure.Reason is { } schemaPath) context.RepairFeedback = $"Lỗi cấu trúc JSON tại {schemaPath}.";
                        schemaRepaired = repaired = true;
                        continue;
                    }
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
                    if (context.ToolsClosed || ++toolRounds > limits.MaxToolRounds || (toolCalls += step.Tools.Length) > limits.MaxToolCalls ||
                        step.Tools.Any(x => x is null || string.IsNullOrWhiteSpace(x.Id)) || step.Tools.Select(x => x.Id).Distinct().Count() != step.Tools.Length)
                        throw Unavailable();
                    var results = new System.Text.Json.JsonElement[step.Tools.Length];
                    for (var index = 0; index < step.Tools.Length; index++)
                    {
                        // Cùng tool + cùng tham số trong một lượt chỉ trả lại dữ liệu đã có. Không chạy lại và đóng tools để
                        // lần gọi LLM kế tiếp phải dùng kết quả hiện có, tránh vòng lặp gọi lại cùng một gợi ý làm tốn quota.
                        if (!executedTools.Add(step.Tools[index].Name + ":" + step.Tools[index].Arguments.GetRawText()))
                        {
                            context.ToolsClosed = true;
                            telemetry.Record("duplicate_tool", context.ConversationId, context.TurnId, context.AttemptId, new { step.Tools[index].Name });
                            results[index] = System.Text.Json.JsonSerializer.SerializeToElement(new { status = "duplicate_request",
                                message = "Identical request already answered above in this turn. Reuse those candidates and return the final output." });
                            continue;
                        }
                        results[index] = await tools.ExecuteAsync(context, step.Tools[index], timeout.Token);
                    }
                    // Giữ liên kết call ID -> result để lần gọi LLM kế tiếp nhận đúng dữ liệu vừa lấy.
                    context.Exchanges.Add(new(step.Tools, results));
                    // Model xin gợi ý khi còn thiếu tỉnh/số ngày/ngân sách: hỏi ngay bằng câu cố định, giữ state vừa nhận
                    // (kể cả khẩu vị người dùng vừa trả lời) và không gọi thêm LLM.
                    if (context.TripDetailsMissing)
                    {
                        var question = AgentReplyBuilder.TripDetailsQuestion(context.State)!;
                        telemetry.Record("trip_details_clarification", context.ConversationId, context.TurnId, context.AttemptId);
                        return new(question, context.State with { PendingQuestion = question }, null, null);
                    }
                    // Một tool call hợp lệ chứng tỏ output trước đã được sửa; không gửi lại thông báo "output không hợp lệ"
                    // ở các lần gọi sau, vì thông báo cũ khiến model lặp lại đúng tool call vừa chạy.
                    repaired = false;
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
                    if (schemaRepaired) throw Unavailable();
                    schemaRepaired = repaired = true;
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
                    context.State = statePolicy.Accept(context.State,
                        AgentMealScopePolicy.Narrow(output.State, context.State, AgentMealScopePolicy.Evidence(context)), confirmedState);
                // Đã có lịch mà người dùng không yêu cầu đổi gì ("ăn vui vui", "ok") và model cũng không trích được ràng buộc mới:
                // không nhận đề xuất đổi món. Giữ nguyên lịch và state đầu lượt, hỏi lại bằng câu chung; lịch hiện tại vẫn lưu được.
                if (output.Action is "create_proposal" or "patch_proposal" && context.Source is not null && !context.ReplanAfterConfirmation &&
                    AgentChangeRequestPolicy.IsUnrequested(context.Request.Content, confirmedState, context.State, context.CatalogTagNames))
                {
                    telemetry.Record("unrequested_change", context.ConversationId, context.TurnId, context.AttemptId, new { output.Action, output.Scope });
                    // Câu hỏi cũ (nếu có) cũng hết hiệu lực: giữ lại nó sẽ chặn "chốt lịch này" bằng một câu hỏi người dùng đã bỏ qua.
                    return new(AgentReplyBuilder.Clarify(context, "general"), confirmedState with { PendingQuestion = ClearedQuestion(confirmedState) }, null, null);
                }
                if (output.Action is "clarify" or "answer")
                {
                    // clarify: thiếu tỉnh/ngày/ngân sách, chưa rõ món hoặc nội dung cần sửa thì hỏi lại.
                    // answer: giải thích bằng ngữ cảnh sẵn có. Cả hai không được kèm items/changes để sửa lịch.
                    // Với prompt "sửa roadmap cho tôi" mà chưa rõ sửa gì, Instructions hướng dẫn clarify;
                    // code ở đây kiểm tra hợp đồng output, không tự hiểu/chấm đúng sai ngữ nghĩa câu hỏi.
                    if (output.Items.Length != 0 || output.Changes.Length != 0) throw Unavailable();
                    // Tên món/quán catalog chưa xác nhận (không thấy, nhiều kết quả, ngoài tỉnh) chưa phải ràng buộc: không giữ trong state,
                    // nếu không lượt sau người dùng chọn quán khác sẽ bị hỏi "xác nhận thay đổi yêu cầu món/quán".
                    if (context.CatalogNotice is not null) context.State = AgentChangeRequestPolicy.WithoutUnsettledNames(context.State, confirmedState);
                    // Model hỏi lại mức/phạm vi dù người dùng vừa trả lời đủ ("Ít, toàn chuyến đi"): cho một lần sửa kèm đúng giá trị
                    // backend đã đọc được, thay vì hỏi người dùng lần nữa. Model vẫn tự lập lịch; backend chỉ nêu câu trả lời.
                    if (output.Action == "clarify" && output.Clarification is "taste_intensity" or "taste_scope" &&
                        tasteResolution is not null && !tasteRepaired && calls + 1 < limits.MaxLlmCalls)
                    {
                        telemetry.Record("taste_answer_repair", context.ConversationId, context.TurnId, context.AttemptId, new { output.Clarification });
                        context.RejectedOutput = output;
                        context.RepairFeedback = tasteResolution;
                        tasteRepaired = repaired = true;
                        continue;
                    }
                    if (output.Action == "clarify" && output.Clarification == "coverage" &&
                        context.ToolNotice is null && AgentReplyBuilder.CoverageGaps(context).Length == 0)
                    {
                        // A model's coverage intent is not evidence that catalog/reco has insufficient choices.
                        telemetry.Record("unsupported_coverage", context.ConversationId, context.TurnId, context.AttemptId);
                        if (!coverageRepaired && calls + 1 < limits.MaxLlmCalls)
                        {
                            context.RepairFeedback = context.RepairFeedback + " Chưa có bằng chứng thiếu lựa chọn cho bữa cụ thể. Không được kết luận thiếu món hoặc yêu cầu người dùng nới điều kiện. " +
                                "Hãy dùng các ứng viên đã lấy để hoàn tất đề xuất; lấy gợi ý riêng cho bữa còn thiếu nếu cần. Giữ các bữa ngoài phạm vi sửa.";
                            coverageRepaired = repaired = true;
                            continue;
                        }
                        var incomplete = lastValidation is { Errors.Length: > 0 }
                            ? AgentReplyBuilder.PlanningIncomplete + " Chi tiết: " + string.Join(" ", lastValidation.Errors)
                            : AgentReplyBuilder.PlanningIncomplete;
                        return new(incomplete, context.State with { PendingQuestion = incomplete[..Math.Min(incomplete.Length, 1000)] }, null, lastValidation);
                    }
                    if (output.Action == "clarify" && output.Clarification == "budget")
                    {
                        // Chỉ hỏi người dùng nới ngân sách khi backend đã thấy một đề xuất bị chặn duy nhất vì vượt ngân sách.
                        if (AgentReplyBuilder.OverBudgetOnly(lastValidation)) return BudgetQuestion(context.State, lastValidation!);
                        telemetry.Record("unsupported_budget", context.ConversationId, context.TurnId, context.AttemptId);
                        if (!budgetRepaired && calls + 1 < limits.MaxLlmCalls)
                        {
                            context.RepairFeedback = context.RepairFeedback + " Chưa có bằng chứng vượt ngân sách. Không được yêu cầu người dùng nới ngân sách. " +
                                "Hãy gửi create_proposal đầy đủ, ưu tiên các món rẻ hơn đã được cấp (so với budgetPerMeal trong kết quả gợi ý); backend sẽ kiểm tra tổng tiền.";
                            budgetRepaired = repaired = true;
                            continue;
                        }
                        return new(AgentReplyBuilder.PlanningIncomplete, context.State with { PendingQuestion = AgentReplyBuilder.PlanningIncomplete }, null, lastValidation);
                    }
                    if (output.Action == "answer" && context.Source is { } current && current.Items.Any(item =>
                        AgentScopedPlanning.RequiresRefresh(current.State, context.State, item)))
                    {
                        if (!proposalRepaired && calls + 1 < limits.MaxLlmCalls)
                        {
                            context.RepairFeedback = "Yêu cầu ngày/bữa đã thay đổi. Cần lấy gợi ý mới cho các bữa bị ảnh hưởng và tạo patch_proposal; không chỉ trả answer.";
                            proposalRepaired = repaired = true;
                            continue;
                        }
                        const string pending = "Yêu cầu đã thay đổi nhưng chưa có đề xuất cập nhật hợp lệ. Cần lấy lại gợi ý cho các ngày/bữa bị ảnh hưởng trước khi lưu.";
                        return new(pending, context.State with { PendingQuestion = pending }, null, null);
                    }
                    if (output.Action == "answer" && context.State.PreferenceApplication == "future" && context.Source is { } futureSource &&
                        futureSource.Items.Any(item => AgentScopedPlanning.Fingerprint(futureSource.State, item.DayNumber, item.MealSlot) !=
                            AgentScopedPlanning.Fingerprint(context.State, item.DayNumber, item.MealSlot)))
                    {
                        var future = futureSource with { State = context.State with { PendingQuestion = null, PendingPlanningRequest = null }, Changes = [],
                            RetainedItems = futureSource.Items, Scope = new() };
                        var checkedFuture = await validator.ValidateAsync(future, futureSource, false, true, timeout.Token, context.Grants);
                        if (checkedFuture.Validation.Errors.Length > 0)
                            return new("Chưa thể giữ nguyên đề xuất theo các điều kiện mới. Vui lòng kiểm tra lại yêu cầu.", context.State, null, checkedFuture.Validation);
                        return new("Đã cập nhật khẩu vị cho các gợi ý tiếp theo. Các món trong bản đề xuất hiện tại được giữ nguyên; lịch chính thức chưa thay đổi.",
                            future.State, checkedFuture.Snapshot, checkedFuture.Validation);
                    }
                    var reply = output.Action == "clarify" ? AgentReplyBuilder.Clarify(context, output.Clarification) : AgentReplyBuilder.Answer(context);
                    // Model đã trả lời thay vì hỏi tiếp nghĩa là câu hỏi cũ không còn mở; câu hỏi khẩu vị còn gắn với yêu cầu đang lập nên giữ.
                    context.State = context.State with { PendingQuestion = output.Action == "clarify" ? reply[..Math.Min(reply.Length, 1000)] : ClearedQuestion(context.State) };
                    return new(reply, context.State, null, null);
                }
                // create_proposal supplies a scoped selection; backend creates or updates from the owned source.
                // patch_proposal supplies explicit add/replace/remove/move operations on that source.
                // Builder kiểm tra nguồn, scope và baseProposalVersion; Validator kiểm tra catalog,
                // nguồn candidate, giá, ngân sách và tuyến. Action hợp lệ chưa có nghĩa proposal hợp lệ.
                context.State = context.State with { PendingQuestion = null, PendingPlanningRequest = null };
                telemetry.Record("proposal_output", context.ConversationId, context.TurnId, context.AttemptId,
                    new { output.Action, output.Scope, output.BaseProposalVersion, itemCount = output.Items.Length,
                        changeCount = output.Changes.Length, sourceItemCount = context.Source?.Items.Length });
                AgentSnapshot snapshot;
                try { snapshot = builder.Build(context, output); }
                catch (ConflictException error) when (error.ErrorCode == "PROPOSAL_NOT_COMMITTABLE" &&
                    output.Items.Length == 0 && output.Changes.Length == 0 && AgentReplyBuilder.OverBudgetOnly(lastValidation))
                {
                    // Model bỏ cuộc (đề xuất rỗng) sau khi lịch chỉ bị chặn vì vượt ngân sách: hỏi người dùng thay vì báo lỗi chung.
                    telemetry.Record("budget_clarification", context.ConversationId, context.TurnId, context.AttemptId, new { lastValidation!.TotalCost });
                    return BudgetQuestion(context.State, lastValidation);
                }
                catch (ConflictException error) when (error.ErrorCode == "PROPOSAL_NOT_COMMITTABLE" &&
                    output.Items.Length == 0 && output.Changes.Length == 0)
                {
                    // Chưa có validation nào nên không có căn cứ cho rằng do ngân sách. Feedback chỉ dựa trên dữ liệu backend thấy:
                    // chưa có ứng viên thì phải lấy gợi ý trước; đã có ứng viên thì phải dùng chúng để gửi đủ món.
                    var noCandidates = context.Grants.Count == 0;
                    var toolsAvailable = !context.ToolsClosed && context.Exchanges.Count < limits.MaxToolRounds;
                    telemetry.Record("empty_proposal", context.ConversationId, context.TurnId, context.AttemptId,
                        new { output.Action, output.Scope, grantCount = context.Grants.Count, context.ToolsClosed, emptyRepaired });
                    // Không còn tools mà chưa có ứng viên thì lần gọi sửa chắc chắn vô ích; không tốn thêm lượt gọi LLM.
                    if (!emptyRepaired && calls + 1 < limits.MaxLlmCalls && (!noCandidates || toolsAvailable))
                    {
                        context.RepairFeedback = error.Message + (noCandidates
                            ? " Chưa có ứng viên nào được cấp trong lượt này. Hãy gọi get_recommendation cho các ngày/bữa trong phạm vi, rồi gửi đề xuất đủ món cho mọi bữa."
                            : " Đề xuất rỗng không được chấp nhận. Hãy gửi đủ món cho mọi bữa trong phạm vi bằng các ứng viên đã được cấp; backend sẽ tự kiểm tra giá và ngân sách.");
                        context.RejectedOutput = output;
                        emptyRepaired = repaired = true;
                        continue;
                    }
                    // Model vẫn bỏ cuộc sau lượt sửa: hỏi lại người dùng thay vì trả lỗi "Lịch đề xuất không hợp lệ".
                    return new(AgentReplyBuilder.PlanningIncomplete, context.State with { PendingQuestion = AgentReplyBuilder.PlanningIncomplete }, null, lastValidation);
                }
                catch (ConflictException error) when (error.ErrorCode == "PROPOSAL_NOT_COMMITTABLE" && !proposalRepaired && calls + 1 < limits.MaxLlmCalls)
                {
                    telemetry.Record("proposal_repair", context.ConversationId, context.TurnId, context.AttemptId,
                        new { output.Action, output.Scope, error.ErrorCode, reason = error.Message });
                    context.RepairFeedback = error.Message;
                    context.RejectedOutput = output;
                    proposalRepaired = repaired = true;
                    continue;
                }
                // Model xóa một bữa theo yêu cầu người dùng: bữa đó được phép trống, nếu không luật đủ bữa sẽ từ chối đúng điều người dùng vừa yêu cầu.
                if (context.Source is { } removalSource && output.Changes.Any(change => change.Type == "remove") && AgentRemovalPolicy.StatesRemoval(context.Request.Content))
                {
                    context.State = AgentRemovalPolicy.WithSkippedMeals(context.State, removalSource, snapshot.Items);
                    snapshot = snapshot with { State = snapshot.State with { SkippedMeals = context.State.SkippedMeals } };
                }
                var validated = await validator.ValidateAsync(snapshot, context.Source, false, true, timeout.Token, context.Grants, context.ReserveGrants);
                telemetry.Record("validation", context.ConversationId, context.TurnId, context.AttemptId, new { validated.Validation.Errors, validated.Validation.Warnings });
                var substituted = 0;
                // Lỗi DUY NHẤT là món nằm ngoài ngày/bữa được cấp: backend thay bằng món xếp hạng cao nhất đã được cấp cho
                // đúng vị trí, cùng khẩu vị/yêu cầu món, không trùng món và trong ngân sách, rồi kiểm tra lại toàn bộ.
                // Không tốn thêm lần gọi LLM; nếu không có món thay an toàn thì giữ luồng model tự sửa như cũ.
                if (validated.Validation.ScopeRejectedItemKeys is { Length: > 0 } rejectedKeys &&
                    await validator.RepairScopeAsync(snapshot, context.Source, context.Grants, rejectedKeys, timeout.Token) is { } repairedSnapshot)
                {
                    var revalidated = await validator.ValidateAsync(repairedSnapshot, context.Source, false, true, timeout.Token, context.Grants, context.ReserveGrants);
                    telemetry.Record("scope_autorepair", context.ConversationId, context.TurnId, context.AttemptId,
                        new { replaced = rejectedKeys.Length, revalidated.Validation.Errors });
                    if (revalidated.Validation.Errors.Length == 0) { validated = revalidated; substituted = rejectedKeys.Length; }
                }
                // Grant dự phòng đứng trước grant chính vì RepairScopeAsync ưu tiên grant cấp sau: món model đã thấy vẫn được chọn trước.
                List<AgentCandidateGrant> repeatGrants = [.. context.ReserveGrants, .. context.Grants];
                // Chỉ vượt ngân sách: backend đổi các món đắt nhất sang món rẻ hơn đã xếp hạng cho đúng vị trí, không hỏi người dùng nới
                // khi catalog vẫn còn món rẻ và không tốn thêm lượt gọi LLM. Hết món rẻ hơn thì mới hỏi như cũ.
                if (AgentReplyBuilder.OverBudgetOnly(validated.Validation) &&
                    await validator.RepairBudgetAsync(validated.Snapshot, context.Source, [.. context.Grants, .. context.ReserveGrants], timeout.Token) is { } cheaper)
                {
                    var revalidated = await validator.ValidateAsync(cheaper.Snapshot, context.Source, false, true, timeout.Token, repeatGrants);
                    telemetry.Record("budget_autorepair", context.ConversationId, context.TurnId, context.AttemptId,
                        new { replaced = cheaper.Replaced, revalidated.Validation.TotalCost, revalidated.Validation.Errors });
                    if (revalidated.Validation.Errors.Length == 0) { validated = revalidated; substituted += cheaper.Replaced; }
                }
                // Món lặp tránh được (còn món chưa dùng đã cấp cho đúng vị trí): backend tự thay, không tốn thêm lượt gọi LLM.
                // Không thay được an toàn thì giữ nguyên lịch hợp lệ; lặp chỉ là ưu tiên đa dạng, không chặn đề xuất.
                if (validated.Validation.Errors.Length == 0 && validated.Validation.AvoidableRepeatItemKeys is { Length: > 0 } repeatKeys &&
                    await validator.RepairScopeAsync(validated.Snapshot, context.Source, repeatGrants, repeatKeys, timeout.Token, repeatsOnly: true) is { } varied)
                {
                    var revalidated = await validator.ValidateAsync(varied, context.Source, false, true, timeout.Token, repeatGrants);
                    telemetry.Record("repeat_autorepair", context.ConversationId, context.TurnId, context.AttemptId,
                        new { replaced = repeatKeys.Length, revalidated.Validation.Errors });
                    if (revalidated.Validation.Errors.Length == 0) { validated = revalidated; substituted += repeatKeys.Length; }
                }
                // Có lỗi thì không trả snapshot mới để lưu Ready; proposal trước được giữ lại.
                if (validated.Validation.Errors.Length > 0)
                {
                    lastValidation = validated.Validation;
                    if (!proposalRepaired && calls + 1 < limits.MaxLlmCalls)
                    {
                        context.RejectedOutput = output;
                        context.RepairFeedback = string.Join(" ", validated.Validation.Errors);
                        proposalRepaired = repaired = true;
                        continue;
                    }
                    if (AgentReplyBuilder.OverBudgetOnly(validated.Validation)) return BudgetQuestion(context.State, validated.Validation);
                    return new("Đề xuất chưa hợp lệ. Vui lòng làm rõ hoặc điều chỉnh các điều kiện.", context.State, null, validated.Validation);
                }
                // Mới là kết quả chuẩn bị. SendAgentMessageUseCase còn kiểm tra lease rồi mới lưu proposal;
                // chỉ writer chung của confirm/chat-save mới áp dụng proposal sau xác nhận rõ ràng.
                var reviewedState = context.State with { FocusDay = null, FocusMeal = null,
                    ReplacedDishNames = AgentScopedPlanning.Replaced(context.State, context.Source, validated.Snapshot),
                    SkippedMeals = AgentRemovalPolicy.Uncovered(context.State.SkippedMeals ?? [], validated.Snapshot.Items) };
                return new(AgentReplyBuilder.Proposal(validated.Snapshot, validated.Validation, substituted: substituted), reviewedState,
                    validated.Snapshot with { State = reviewedState }, validated.Validation);
            }
            // Đã dùng hết số lần gọi cho phép mà chưa có kết quả cuối hợp lệ.
            throw Unavailable();
        }
        catch (AgentStateChangeRequiredException change)
        { return AgentChatPolicy.StageChange(confirmedState, change.Proposed, context.Source is not null || context.Roadmap is not null); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new AgentProviderException(new(AgentLlmFailureKind.TurnTimeout)); }
        catch (FluentValidation.ValidationException) { throw Unavailable(); }
    }

    /// <summary>Removes the named items from the source, validates the remainder and returns it as a new preview without a provider call.</summary>
    private async Task<AgentPreparedTurn> RemoveAsync(AgentContext context, AgentItem[] removed, CancellationToken ct)
    {
        var source = context.Source!;
        var remaining = source.Items.Except(removed).ToArray();
        // Bản xem trước chưa lưu không thể rỗng; roadmap đã lưu thì được phép (xem luật lịch rỗng trong validator).
        if (remaining.Length == 0 && !source.BaseRoadmapId.HasValue)
            return new("Đây là toàn bộ món trong bản xem trước. Nếu muốn bỏ cả bản này, nhắn ‘bỏ đề xuất’.", context.State with { PendingQuestion = null }, null, null);
        var state = AgentRemovalPolicy.WithSkippedMeals(context.State with { PendingQuestion = null, FocusDay = null, FocusMeal = null }, source, remaining);
        state = state with { ReplacedDishNames = AgentScopedPlanning.Replaced(state, source, source with { Items = remaining }) };
        var snapshot = source with { State = state, Items = remaining, RetainedItems = remaining, Scope = new(),
            Changes = removed.Select(item => new AgentChange("remove", item.ItemKey, null)).ToArray() };
        var checkedRemoval = await validator.ValidateAsync(snapshot, source, false, true, ct, context.Grants);
        if (checkedRemoval.Validation.Errors.Length > 0)
            return new("Chưa thể bỏ món này: " + string.Join(" ", checkedRemoval.Validation.Errors), context.State, null, checkedRemoval.Validation);
        var names = string.Join(", ", removed.Select(item => AgentReplyBuilder.Safe(item.DishName)).Distinct());
        return new($"Đã bỏ {names} khỏi bản xem trước.\n" + AgentReplyBuilder.Proposal(checkedRemoval.Snapshot, checkedRemoval.Validation),
            state, checkedRemoval.Snapshot with { State = state }, checkedRemoval.Validation);
    }

    /// <summary>A superseded question is dropped; a taste question stays because it belongs to the planning request being clarified.</summary>
    private static string? ClearedQuestion(AgentState state) =>
        AgentTasteClarificationPolicy.IsQuestion(state.PendingQuestion) ? state.PendingQuestion : null;

    /// <summary>Asks the user how to resolve a budget the rejected proposal exceeded; the previous ready proposal stays.</summary>
    private static AgentPreparedTurn BudgetQuestion(AgentState state, AgentValidation validation)
    {
        var question = AgentReplyBuilder.BudgetShortfall(state, validation);
        return new(question, state with { PendingQuestion = question[..Math.Min(question.Length, 1000)] }, null, validation);
    }

    /// <summary>Creates a controlled limit/contract error without replacing the last ready proposal.</summary>
    // Các nguyên nhân ở đây (hết vòng tool, output sai hợp đồng, context quá lớn) lặp lại nếu gửi lại y nguyên,
    // nên không mời "thử lại"; lỗi provider tạm thời đi qua AgentProviderException với thông báo riêng.
    private static ServiceUnavailableException Unavailable() => new("AGENT_UNAVAILABLE",
        "Agent chưa hoàn tất yêu cầu này. Bạn có thể nói rõ hơn hoặc điều chỉnh yêu cầu (ngày/bữa, món, ngân sách hoặc khẩu vị).");
}

/// <summary>Prepared turn data that must pass lease fencing before persistence.</summary>
public sealed record AgentPreparedTurn(string Reply, AgentState State, AgentSnapshot? Snapshot, AgentValidation? Validation,
    string? Action = null, AgentCommitPreparation? Commit = null);
