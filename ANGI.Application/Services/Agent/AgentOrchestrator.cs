using System.Diagnostics;
using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Services.Agent;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Services.Agent.Constraints;
using ANGI.Application.Services.Agent.Conversation;
using ANGI.Application.Services.Agent.Intake;
using ANGI.Application.Services.Agent.Places;
using ANGI.Application.Services.Agent.Proposals;

namespace ANGI.Application.Services.Agent;

/// <summary>Runs bounded LLM/tool rounds outside a database transaction and prepares validated immutable output.</summary>
public sealed class AgentOrchestrator(IAgentLlmService llm, AgentToolExecutor tools, AgentStatePolicy statePolicy,
    AgentProposalBuilder builder, AgentProposalValidator validator, AgentLimits limits, IAgentTelemetry telemetry,
    Common.Interfaces.Repositories.Agent.IAgentCatalogRepository? catalog = null, AgentPlaceResolver? places = null)
{
    /// <summary>Bounds schema, proposal and unsupported-coverage repairs within the same call and timeout limits.</summary>
    public async Task<AgentPreparedTurn> RunAsync(AgentContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // "Xin chào", "cảm ơn", "bạn là ai": trả lời luôn, không tốn lần gọi LLM và không đụng tới lịch.
        if (AgentSmallTalkPolicy.FixedReply(context) is { } smallTalk)
        {
            telemetry.Record("small_talk", context.ConversationId, context.TurnId, context.AttemptId);
            return new(smallTalk, context.State, null, null, "chat");
        }
        await UsePinnedAreaAsync(context, ct);
        // Giữ state đầu lượt: lời chào hoặc câu ngoài phạm vi không được làm mất câu hỏi đang mở hay lọt vào yêu cầu lập lịch.
        var turnState = context.State;
        // Lịch mới: hỏi lần lượt địa điểm, số ngày và ngân sách, rồi khẩu vị/yêu cầu; không tốn lần gọi LLM cho tới khi đủ điều kiện lập lịch.
        if (await IntakeAsync(context, ct) is { } intake)
        {
            telemetry.Record("intake", context.ConversationId, context.TurnId, context.AttemptId, new { stage = intake.State.PlanningIntake });
            return intake;
        }
        if (await ItineraryChangeAsync(context, ct) is { } itineraryTurn) return itineraryTurn;
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
            // Chưa có lịch nào thì không có gì để chốt.
            return new(AgentReplyBuilder.Clarify(context, "general") + (AgentIntakePolicy.HasPlan(context) ? " Ưng rồi thì cứ bảo mình chốt nhé." : ""), context.State, null, null);
        }
        // "Món lạ/món mới/món chưa ăn" không có tag nào tương ứng: hỏi nghĩa trước, không để model tra nó như tên món.
        if (AgentTasteClarificationPolicy.NoveltyQuestion(context) is { } noveltyQuestion)
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
        var intakeRepaired = false;
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
                        return new(question, context.State with { PendingQuestion = question, PlanningIntake = AgentIntakePolicy.HasPlan(context) ? null : AgentIntakePolicy.Details }, null, null);
                    }
                    // Hội thoại 94 tốn thêm 2 lần gọi LLM vì model xin tiếp ứng viên dù đã đủ món cho mọi bữa.
                    if (AgentIntakePolicy.PlanCovered(context)) context.ToolsClosed = true;
                    // Một tool call hợp lệ chứng tỏ output trước đã được sửa; không gửi lại thông báo "output không hợp lệ"
                    // ở các lần gọi sau, vì thông báo cũ khiến model lặp lại đúng tool call vừa chạy.
                    repaired = false;
                    continue;
                }
                var output = step.Output!;
                // Action do LLM chọn theo Instructions trong adapter; hợp đồng chỉ cho phép các action
                // được khai báo ở AgentLlmSchema.Output, đồng thời kiểm tra lại allowlist tại đây.
                // Đây là quy tắc xử lý của ANGI, không có điểm số confidence/threshold phân loại intent riêng.
                // Reply rỗng không phải lỗi: backend có câu cố định cho mọi action. Model hay để rỗng với out_of_scope vì câu từ chối là của backend;
                // từng coi đó là output hỏng khiến "hướng dẫn tôi" tốn 2-4 lần gọi LLM rồi trả 503 cho người dùng.
                if (output.Reply is null || output.Reply.Length > limits.MaxMessageLength ||
                    output.Clarification is not null && !AgentReplyBuilder.Clarifications.Contains(output.Clarification) ||
                    output.Items is null || output.Changes is null || output.Scope is null ||
                    output.Action is not ("clarify" or "answer" or "create_proposal" or "patch_proposal" or "discard_proposal" or "reset_conversation" or "chat" or "out_of_scope"))
                {
                    // Chỉ ghi tên trường và độ dài, không ghi nội dung model trả về.
                    telemetry.Record("invalid_output", context.ConversationId, context.TurnId, context.AttemptId,
                        new { output.Action, output.Clarification, replyLength = output.Reply?.Length, schemaRepaired });
                    if (schemaRepaired) return Misunderstood(context, turnState);
                    schemaRepaired = repaired = true;
                    continue;
                }
                // Dừng trước khi nhận state của model: câu lạc đề không được đổi điều kiện chuyến đi, và mọi items/changes kèm theo đều bị bỏ qua.
                if (output.Action is "chat" or "out_of_scope")
                {
                    telemetry.Record(output.Action, context.ConversationId, context.TurnId, context.AttemptId);
                    return new(output.Action == "chat" ? AgentSmallTalkPolicy.ChatReply(output.Reply, turnState)
                        : AgentSmallTalkPolicy.OutOfScopeReply(context.Request.Content, output.SuggestedDish), turnState, null, null, output.Action);
                }
                // Model có thể bổ sung/siết điều kiện, nhưng không được tự nới ràng buộc đã chốt.
                // Cờ ConstraintsChangeConfirmed không cấp quyền; backend giữ pending change để người dùng xác nhận.
                if (output.Action is "discard_proposal" or "reset_conversation")
                {
                    if (output.Items.Length != 0 || output.Changes.Length != 0 || output.State is not null) return Misunderstood(context, turnState);
                    return new(output.Action == "discard_proposal" ? "Mình đã bỏ bản nháp này rồi. Lịch đã lưu vẫn được giữ nguyên."
                        : "Mình bắt đầu lại từ đầu nhé. Lịch đã lưu vẫn được giữ nguyên.", context.State, null, null,
                        output.Action == "discard_proposal" ? "discard" : "reset");
                }
                if (!context.ReplanAfterConfirmation)
                    context.State = statePolicy.Accept(context.State, AgentPendingPlanningPolicy.ApplyStated(AgentNamedRequestPolicy.Retain(
                        AgentMealScopePolicy.Narrow(AgentStatePolicy.WithoutErased(AgentIntakePolicy.WithoutUnstatedProvince(output.State, context), context.State, context.Request.Content), context.State, AgentMealScopePolicy.Evidence(context)), context.PlacedRequests), context.Request.Content), context.ConstraintBaseline);
                // Đã có lịch mà người dùng không yêu cầu đổi gì ("ăn vui vui", "ok") và model cũng không trích được ràng buộc mới:
                // không nhận đề xuất đổi món. Giữ nguyên lịch và state đầu lượt, hỏi lại bằng câu chung; lịch hiện tại vẫn lưu được.
                if (output.Action is "create_proposal" or "patch_proposal" && context.Source is { Items.Length: > 0 } && !context.ReplanAfterConfirmation && !context.ItineraryChanged &&
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
                    if (output.Items.Length != 0 || output.Changes.Length != 0)
                    {
                        // Câu hỏi/trả lời không bao giờ sửa lịch: bỏ phần món kèm theo thay vì làm hỏng cả lượt chat.
                        telemetry.Record("ignored_items", context.ConversationId, context.TurnId, context.AttemptId, new { output.Action, itemCount = output.Items.Length, changeCount = output.Changes.Length });
                        output = output with { Items = [], Changes = [] };
                    }
                    // Người dùng vừa trả lời xong phần hỏi trước khi lập lịch: model không hỏi lại chung chung, cho một lần sửa để lập lịch.
                    if (context.IntakeComplete && !intakeRepaired && calls + 1 < limits.MaxLlmCalls && output.Clarification is null or "general" or "trip_details" &&
                        AgentReplyBuilder.TripDetailsQuestion(context.State) is null)
                    {
                        telemetry.Record("intake_repair", context.ConversationId, context.TurnId, context.AttemptId, new { output.Action, output.Clarification });
                        context.RepairFeedback = "Người dùng đã trả lời đủ địa điểm, số ngày, ngân sách và yêu cầu riêng. Không hỏi lại; hãy gọi get_recommendation scope=full rồi gửi create_proposal đủ món.";
                        intakeRepaired = repaired = true;
                        continue;
                    }
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
                    var reply = AgentGroundedReply.Choose(context, output);
                    // Model đã trả lời thay vì hỏi tiếp nghĩa là câu hỏi cũ không còn mở; câu hỏi khẩu vị còn gắn với yêu cầu đang lập nên giữ.
                    context.State = context.State with { PendingQuestion = output.Action == "clarify" ? reply[..Math.Min(reply.Length, 1000)] : ClearedQuestion(context.State) };
                    // Model hỏi thông tin chuyến đi: các câu trả lời tiếp theo được đọc theo thứ tự cố định, không cần gọi LLM.
                    if (output.Action == "clarify" && !AgentIntakePolicy.HasPlan(context) && AgentReplyBuilder.TripDetailsQuestion(context.State) is not null)
                        context.State = context.State with { PlanningIntake = AgentIntakePolicy.Details };
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
                catch (ConflictException error) when (error.ErrorCode == "PROPOSAL_NOT_COMMITTABLE")
                {
                    // Model vẫn gửi đề xuất sai sau lượt sửa: lỗi của model, không phải của người dùng, và không được thành lỗi 409 "bản nháp không đủ điều kiện".
                    // Lịch mới thì backend tự ghép từ các món đã được cấp, không tốn thêm lần gọi LLM; còn lại giữ nguyên bản đang có.
                    telemetry.Record("proposal_unusable", context.ConversationId, context.TurnId, context.AttemptId,
                        new { output.Action, output.Scope, reason = error.Message });
                    if (AgentProposalBuilder.Assemble(context) is not { } assembled)
                        return new(AgentReplyBuilder.PlanningIncomplete, confirmedState with { PendingQuestion = ClearedQuestion(confirmedState) }, null, lastValidation);
                    output = assembled;
                    snapshot = builder.Build(context, output);
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
                    return new("Mình chưa sắp được lịch phù hợp. Bạn thử nói rõ hơn hoặc điều chỉnh yêu cầu giúp mình nhé.", context.State, null, validated.Validation);
                }
                // Mới là kết quả chuẩn bị. SendAgentMessageUseCase còn kiểm tra lease rồi mới lưu proposal;
                // chỉ writer chung của confirm/chat-save mới áp dụng proposal sau xác nhận rõ ràng.
                context.PreviewOutput = output;
                var reviewedState = context.State with { FocusDay = null, FocusMeal = null,
                    ReplacedDishNames = AgentScopedPlanning.Replaced(context.State, context.Source, validated.Snapshot),
                    SkippedMeals = AgentRemovalPolicy.Uncovered(context.State.SkippedMeals ?? [], validated.Snapshot.Items) };
                return new(AgentReplyBuilder.Proposal(validated.Snapshot, substituted, context.Source, AgentGroundedReply.Opener(output.Reply)), reviewedState,
                    validated.Snapshot with { State = reviewedState }, validated.Validation);
            }
            // Đã dùng hết số lần gọi cho phép mà chưa có kết quả cuối hợp lệ.
            throw Unavailable();
        }
        catch (AgentStateChangeRequiredException change)
        { return AgentChatPolicy.StageChange(confirmedState, change.Proposed, context.Source is not null || context.Roadmap is not null); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new AgentProviderException(new(AgentLlmFailureKind.TurnTimeout)); }
        // State do model đề xuất sai luật: lỗi của model, không phải của người dùng. Giữ state đầu lượt và hỏi lại.
        catch (FluentValidation.ValidationException) { return Misunderstood(context, turnState); }
    }

    /// <summary>
    /// A point pinned on the map already says where the user will eat, so a new plan never asks again: it gives the province and the search circle.
    /// Only a state with no province takes these, so a user who later widened the search is not narrowed again.
    /// </summary>
    private async Task UsePinnedAreaAsync(AgentContext context, CancellationToken ct)
    {
        var state = context.State;
        if (places is null || state.OriginMode != "pinned" || state.OriginLatitude is not { } latitude || state.OriginLongitude is not { } longitude ||
            !string.IsNullOrWhiteSpace(state.Province) || state.PendingStateChange is not null || AgentIntakePolicy.HasPlan(context)) return;
        if (await places.AroundPointAsync(latitude, longitude, ct) is not { } area)
        {
            context.PinNote = "Quanh điểm bạn chấm trên bản đồ chưa có quán nào trên ANGI.";
            return;
        }
        context.State = state with { Province = area.Province, RadiusKm = state.RadiusKm ?? area.RadiusKm };
        context.PinNote = $"Mình sẽ tìm quán trong khoảng {context.State.RadiusKm:0} km quanh điểm bạn đã chấm trên bản đồ.";
        telemetry.Record("pinned_area", context.ConversationId, context.TurnId, context.AttemptId, new { context.State.RadiusKm });
    }

    /// <summary>Runs the pre-plan questions; null means the turn continues through the normal flow, possibly with the intake just closed.</summary>
    private async Task<AgentPreparedTurn?> IntakeAsync(AgentContext context, CancellationToken ct)
    {
        if (catalog is null || !AgentIntakePolicy.Eligible(context)) return null;
        var provinces = await catalog.GetProvincesAsync(ct);
        var nearby = AgentIntakePolicy.NeedsNearby(context)
            ? await catalog.NearestProvinceAsync(context.State.OriginLatitude!.Value, context.State.OriginLongitude!.Value, AgentIntakePolicy.MaxNearbyKm, ct) : null;
        var trip = places is null ? null : await places.ReadAsync(context, provinces, ct);
        if (AgentIntakePolicy.Read(context, provinces, nearby, trip, places is not null) is not { } read) return null;
        if (read.Reply is { } fixedReply)
        {
            // Tọa độ không dùng được thì không giữ lại: state không được mang điểm xuất phát mà chuyến đi không dùng.
            var kept = AgentIntakePolicy.NeedsNearby(context) ? read.Current with { OriginMode = "none", OriginLatitude = null, OriginLongitude = null, OriginLabel = null, RadiusKm = null } : read.Current;
            return new(fixedReply, kept with { PendingQuestion = AgentIntakePolicy.PlaceQuestion }, null, null);
        }
        AgentState accepted;
        try { accepted = statePolicy.Accept(read.Current, read.Proposed, context.State); }
        // Chưa có lịch nào bị ảnh hưởng và giá trị đến từ chính câu của người dùng (không qua model), nên không cần hỏi xác nhận lại.
        catch (AgentStateChangeRequiredException change)
        {
            accepted = change.Proposed with { PendingStateChange = read.Current.PendingStateChange, SourceRoadmapUpdatedAt = read.Current.SourceRoadmapUpdatedAt,
                HistoryStartSequence = read.Current.HistoryStartSequence };
        }
        catch (FluentValidation.ValidationException error)
        {
            var problem = error.Errors.FirstOrDefault()?.ErrorMessage ?? "Thông tin chuyến đi chưa hợp lệ.";
            return new($"{problem} Bạn nhắn lại giúp mình nhé.", context.State with { PlanningIntake = AgentIntakePolicy.Details }, null, null);
        }
        var step = AgentIntakePolicy.Next(context, accepted, read, provinces);
        if (!step.Closed) return new(step.Reply!, step.State, null, null);
        context.State = step.State;
        context.IntakeComplete = step.Plan;
        return null;
    }

    /// <summary>
    /// Reads where an existing plan moves without asking the model to read the place. "Ngày 2 tôi ở Cầu Giấy" moves that day; "đổi sang khu Cầu Giấy" names no
    /// day, so it asks which days instead of guessing. A returned turn ends here; null continues to the model with the moved days set.
    /// Days past the trip stay with the model, because extending a saved duration needs the user's confirmation.
    /// </summary>
    private async Task<AgentPreparedTurn?> ItineraryChangeAsync(AgentContext context, CancellationToken ct)
    {
        if (places is null || catalog is null || context.Request.Command != "chat" || !AgentIntakePolicy.HasPlan(context) || context.State.PendingStateChange is not null) return null;
        var content = context.Request.Content;
        // Trả lời câu hỏi "ngày nào". Câu khác thì câu hỏi hết hiệu lực và tin nhắn đi tiếp như bình thường, để người dùng không bị kẹt ở câu hỏi này.
        if (context.State.PendingDayArea is { } pending)
        {
            context.State = context.State with { PendingDayArea = null, PendingQuestion = null };
            if (AgentItineraryPolicy.AnsweredDays(content, context.State.NumDays ?? 0) is { Length: > 0 } days)
            {
                var areas = days.Select(day => new AgentDayArea(day, pending.Label, pending.Latitude, pending.Longitude, pending.RadiusKm, pending.District)).ToArray();
                return Move(context, areas, []) ? null : new($"Những ngày đó đã ở khu vực {AgentReplyBuilder.Safe(pending.Label)} rồi nên mình giữ nguyên lịch nhé.", context.State, null, null);
            }
        }
        // Người dùng báo sai địa điểm: tự sửa nếu câu có nơi đúng, không thì nói rõ đang tìm ở đâu và cách sửa, không để model đoán rồi lấy lại đúng vùng cũ.
        if (AgentItineraryPolicy.Correction(content, context.State) is { } correction)
        {
            telemetry.Record("itinerary_correction", context.ConversationId, context.TurnId, context.AttemptId, new { correction.Days, hasPlace = correction.Place is not null });
            var corrected = correction.Place is { } name ? await places.LocateAsync(name, context.State.Province, true, await catalog.GetProvincesAsync(ct), ct) : null;
            if (corrected is not null && correction.Days.Length > 0)
            {
                var areas = correction.Days.Select(day => new AgentDayArea(day, corrected.Label, corrected.Latitude, corrected.Longitude, corrected.RadiusKm, corrected.District)).ToArray();
                if (Move(context, areas, [])) return null;
            }
            else if (corrected is not null) return WhichDayQuestion(context, corrected);
            return new(AgentItineraryPolicy.CorrectionHelp(context.State, correction.Days, corrected is null ? correction.Place : null), context.State, null, null);
        }
        if (AgentItineraryPolicy.Stops(content).Length > 0)
        {
            if (await places.ReadAsync(context, await catalog.GetProvincesAsync(ct), ct) is { } trip)
                Move(context, trip.DayAreas.Where(area => area.DayNumber <= context.State.NumDays).ToArray(), trip.WholeProvinceDays.Where(day => day <= context.State.NumDays).ToArray());
            return null;
        }
        if (AgentItineraryPolicy.BareArea(content) is not { } bare || context.State.NumDays is not { } numDays ||
            await places.LocateAsync(bare.Name, context.State.Province, bare.Searchable, await catalog.GetProvincesAsync(ct), ct) is not { } place) return null;
        // Chuyến một ngày thì không có gì để hỏi.
        if (numDays == 1)
        {
            Move(context, [new(1, place.Label, place.Latitude, place.Longitude, place.RadiusKm, place.District)], []);
            return null;
        }
        return WhichDayQuestion(context, place);
    }

    /// <summary>Asks which days move to a place named without a day, and holds the place for the answer.</summary>
    private AgentPreparedTurn WhichDayQuestion(AgentContext context, AgentPlaceMatch place)
    {
        var question = $"Bạn muốn đổi ngày nào sang khu vực {AgentReplyBuilder.Safe(place.Label)}? Bạn nhắn ví dụ “ngày 2”, “ngày 1 và 2” hoặc “cả chuyến” nhé.";
        telemetry.Record("itinerary_day_clarification", context.ConversationId, context.TurnId, context.AttemptId);
        return new(question, context.State with { PendingQuestion = question, PendingDayArea = place }, null, null);
    }

    /// <summary>Applies new day places and records which days changed; false when nothing changed, so an unchanged itinerary never forces a replan.</summary>
    private bool Move(AgentContext context, AgentDayArea[] areas, short[] wholeProvince)
    {
        var updated = AgentItineraryPolicy.Apply(context.State, new(null, areas, null, wholeProvince, [], 0));
        if (AgentJson.Write(updated.DayAreas) == AgentJson.Write(context.State.DayAreas)) return false;
        context.MovedDays = areas.Select(area => area.DayNumber).Concat(wholeProvince).ToArray();
        telemetry.Record("itinerary_change", context.ConversationId, context.TurnId, context.AttemptId, new { days = context.MovedDays });
        context.State = updated;
        return true;
    }

    /// <summary>A fixed question for output the backend cannot use; the turn state is unchanged and the user is never shown a service error for it.</summary>
    private AgentPreparedTurn Misunderstood(AgentContext context, AgentState state)
    {
        telemetry.Record("misunderstood", context.ConversationId, context.TurnId, context.AttemptId);
        return new(AgentReplyBuilder.TripDetailsQuestion(state) is { } question && !AgentIntakePolicy.HasPlan(context) ? question : AgentSmallTalkPolicy.NotUnderstood, state, null, null);
    }

    /// <summary>Removes the named items from the source, validates the remainder and returns it as a new preview without a provider call.</summary>
    private async Task<AgentPreparedTurn> RemoveAsync(AgentContext context, AgentItem[] removed, CancellationToken ct)
    {
        var source = context.Source!;
        var remaining = source.Items.Except(removed).ToArray();
        // Bản xem trước chưa lưu không thể rỗng; roadmap đã lưu thì được phép (xem luật lịch rỗng trong validator).
        if (remaining.Length == 0 && !source.BaseRoadmapId.HasValue)
            return new("Đây là toàn bộ món trong bản nháp rồi. Nếu muốn bỏ hết, bạn nhắn “bỏ đề xuất” nhé.", context.State with { PendingQuestion = null }, null, null);
        var state = AgentRemovalPolicy.WithSkippedMeals(context.State with { PendingQuestion = null, FocusDay = null, FocusMeal = null }, source, remaining);
        state = state with { ReplacedDishNames = AgentScopedPlanning.Replaced(state, source, source with { Items = remaining }) };
        var snapshot = source with { State = state, Items = remaining, RetainedItems = remaining, Scope = new(),
            Changes = removed.Select(item => new AgentChange("remove", item.ItemKey, null)).ToArray() };
        var checkedRemoval = await validator.ValidateAsync(snapshot, source, false, true, ct, context.Grants);
        if (checkedRemoval.Validation.Errors.Length > 0)
            return new("Chưa thể bỏ món này: " + string.Join(" ", checkedRemoval.Validation.Errors), context.State, null, checkedRemoval.Validation);
        return new(AgentReplyBuilder.Proposal(checkedRemoval.Snapshot, source: source),
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
        "Mình chưa xử lý xong yêu cầu này. Bạn thử nói rõ hơn hoặc điều chỉnh ngày, bữa, món, ngân sách hay khẩu vị giúp mình nhé.");
}

/// <summary>Prepared turn data that must pass lease fencing before persistence.</summary>
public sealed record AgentPreparedTurn(string Reply, AgentState State, AgentSnapshot? Snapshot, AgentValidation? Validation,
    string? Action = null, AgentCommitPreparation? Commit = null);
