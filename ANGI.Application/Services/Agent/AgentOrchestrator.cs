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
public sealed class AgentOrchestrator(IAgentLlmService llm, AgentToolExecutor tools, AgentConstraintGuard statePolicy,
    AgentProposalBuilder builder, AgentProposalValidator validator, AgentLimits limits, IAgentTelemetry telemetry,
    Common.Interfaces.Repositories.Agent.IAgentCatalogRepository? catalog = null, AgentPlaceResolver? places = null)
{
    /// <summary>Bounds schema, proposal and unsupported-coverage repairs within the same call and timeout limits.</summary>
    public async Task<AgentPreparedTurn> RunAsync(AgentContext context, CancellationToken ct)
    {
        // STEP-01: Call ct.ThrowIfCancellationRequested.
        ct.ThrowIfCancellationRequested();
        // STEP-02: Call UsePinnedAreaAsync.
        await UsePinnedAreaAsync(context, ct);
        // STEP-03: Return the result of StartIntakeAsync early when the place of a new trip already has its fixed answer.
        // Lịch mới: model đọc câu trả lời và viết câu hỏi; backend chỉ giữ thứ tự địa điểm, số ngày và ngân sách, rồi khẩu vị/yêu cầu.
        if (await StartIntakeAsync(context, ct) is { } intake) return intake;
        // STEP-04: Prepare turnState.
        // Giữ state đầu lượt: lời chào hoặc câu ngoài phạm vi không được làm mất câu hỏi đang mở hay lọt vào yêu cầu lập lịch.
        var turnState = context.State;
        // STEP-06: When context.State.PendingPlanningRequest exists, set context.State.
        if (context.State.PendingPlanningRequest is { } pendingRequest)
            context.State = context.State with { PendingPlanningRequest = AgentIntakePolicy.CaptureRequest(pendingRequest, context.Request.Content) };
        // STEP-08: Prepare timeout, then call timeout.CancelAfter.
        // Timeout áp dụng cho toàn bộ vòng LLM/tools của lượt chat; ct vẫn giữ quyền hủy từ caller.
        // Use case đã commit bước claim trước khi vào đây nên không giữ transaction DB trong lúc chờ AI.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(limits.TimeoutSeconds));
        // STEP-09: Prepare toolRounds, toolCalls, repaired, schemaRepaired and 11 more.
        var toolRounds = 0;
        var toolCalls = 0;
        var repaired = false;
        var schemaRepaired = false;
        var proposalRepaired = false;
        // Đề xuất rỗng có lượt sửa riêng để không tiêu mất lượt sửa lỗi validation của lịch đủ món sau đó.
        var emptyRepaired = false;
        var coverageRepaired = false;
        var budgetRepaired = false;
        var budgetQuoteRepaired = false;
        var tasteRepaired = false;
        var intakeRepaired = false;
        var replyRepaired = false;
        AgentValidation? lastValidation = null;
        var transientRetries = 0;
        var executedTools = new HashSet<string>(StringComparer.Ordinal);
        var turnStarted = Stopwatch.GetTimestamp();
        var confirmedState = context.ConstraintBaseline = context.State;
        // STEP-10: When no plan exists, set context.ConstraintBaseline.
        // Chưa có lịch nào bị ảnh hưởng: nêu hay sửa địa điểm, số ngày, ngân sách, kể cả để ngân sách mở, không cần hỏi xác nhận thêm.
        if (AgentIntakePolicy.Eligible(context)) context.ConstraintBaseline = AgentIntakePolicy.Baseline(context.State);
        // STEP-11: Update context: PromptSource.
        context.PromptSource = AgentProposalBuilder.PromptSource(context);
        // Model đang có tool trong tay hay gọi tool kể cả khi không cái nào phù hợp, nên một tin nhắn chat được đọc trước khi có tool. Nút bấm, form và lập lại lịch đã rõ việc nên không cần bước này.
        var phase = context.Request.Command == "chat" && context.Request.ConfirmedState is null && !context.ReplanAfterConfirmation ? AgentTurnPhase.Read : AgentTurnPhase.Act;
        var sameRead = false;
        try
        {
            // STEP-13: Repeat while calls < limits.MaxLlmCalls.
            for (var calls = 0; calls < limits.MaxLlmCalls; calls++)
            {
                // STEP-14: Call timeout.Token.ThrowIfCancellationRequested.
                // Mỗi vòng có thể là một lần model xin tools hoặc trả output cuối; đều tính vào MaxLlmCalls.
                timeout.Token.ThrowIfCancellationRequested();
                // Sửa lỗi cấu trúc hay thử lại do provider vẫn là cùng một lần đọc; các lần sửa khác có thể cần tool nên có đủ tool.
                if (phase == AgentTurnPhase.Read && repaired && !sameRead) phase = AgentTurnPhase.Act;
                sameRead = false;
                context.Phase = phase;
                // STEP-15: Throw the error from Unavailable when AgentJson.Write().Length > limits.MaxContextCharacters.
                if (AgentJson.Write(new { context.State, Source = context.PromptSource ?? context.Source, context.History, context.Request, context.Exchanges, context.RejectedOutput }).Length > limits.MaxContextCharacters)
                    throw Unavailable();
                // STEP-16: Prepare started through Stopwatch.GetTimestamp and step.
                var started = Stopwatch.GetTimestamp();
                // ĐÂY LÀ DÒNG GỌI LLM qua IAgentLlmService (DI nối tới OpenAiAgentLlmService).
                // Adapter dựng system prompt + context + strict schema rồi gửi HTTP tới provider.
                // API key được gắn vào Authorization header trong adapter, không nằm trong AgentContext.
                AgentLlmStep? step;
                // STEP-17: Call llm.RunAsync and keep step.
                try { step = await llm.RunAsync(context, repaired, timeout.Token); }
                catch (OperationCanceledException)
                {
                    // STEP-18: On OperationCanceledException, call telemetry.Record and rethrow the caught exception.
                    telemetry.Record("llm", context.ConversationId, context.TurnId, context.AttemptId, new
                    {
                        elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds, callNumber = calls + 1,
                        repair = repaired, outcome = ct.IsCancellationRequested ? "caller_cancelled" : "turn_timeout"
                    });
                    throw;
                }
                // STEP-19: When step is missing or (step.Failure is missing and (step.Tools is missing, (step.Tools.Length is 0 and more.
                if (step is null || step.Failure is null && (step.Tools is null || step.Tools.Length == 0 && step.Output is null || step.Tools.Length > 0 && step.Output is not null))
                    step = AgentLlmStep.Failed(AgentLlmFailureKind.InvalidResponse);
                // STEP-20: Call telemetry.Record.
                telemetry.Record("llm", context.ConversationId, context.TurnId, context.AttemptId, new
                {
                    elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds, step.Tokens, callNumber = calls + 1,
                    repair = repaired, limits.MaxOutputTokens, limits.ReasoningEffort,
                    schemaRepaired, proposalRepaired, emptyRepaired, coverageRepaired,
                    failureKind = step.Failure?.Kind.ToString(), step.Failure?.HttpStatus, step.Failure?.RetryAfterSeconds,
                    step.Failure?.ProviderCode, step.Failure?.Reason
                });
                // STEP-21: Return the result of SuggestionTurnAsync early when context.DishSuggestion exists.
                // Món đã được chọn xong, lần gọi này chỉ để viết câu: lỗi hay output hỏng đều dùng câu cố định, không gọi thêm và không báo lỗi cho người dùng.
                if (context.DishSuggestion is { } suggestion) return await SuggestionTurnAsync(context, suggestion, step.Output, turnState, timeout.Token);
                // STEP-22: When step.Failure exists, skip this round when failure.Kind is InvalidSchema and schemaRepaired is false and more.
                // Repair a malformed schema once; planning repairs have independent bounded attempts under the same deadline.
                if (step.Failure is { } failure)
                {
                    if (failure.Kind == AgentLlmFailureKind.InvalidSchema && !schemaRepaired)
                    {
                        // Báo đúng đường dẫn trường sai để lần sửa duy nhất có cơ hội thành công thay vì đoán lại toàn bộ.
                        if (failure.Reason is { } schemaPath) context.RepairFeedback = $"Lỗi cấu trúc JSON tại {schemaPath}.";
                        schemaRepaired = repaired = sameRead = true;
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
                // STEP-23: When step.Tools.Length > 0, throw the error from Unavailable when context.ToolsClosed is true, ++toolRounds > limits.MaxToolRounds and more.
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
                    phase = AgentTurnPhase.Act;
                    context.ForcedTool = null;
                    // Model viết câu trả lời từ kết quả gợi ý ở lần gọi kế tiếp; tools đóng lại nên đây là lần gọi cuối của lượt.
                    if (context.DishSuggestion is { } found)
                    {
                        if (calls + 1 >= limits.MaxLlmCalls) return await SuggestionTurnAsync(context, found, null, turnState, timeout.Token);
                        context.ToolsClosed = true;
                        repaired = false;
                        continue;
                    }
                    // Model xin gợi ý khi còn thiếu tỉnh/số ngày/ngân sách: hỏi ngay bằng câu cố định, giữ state vừa nhận
                    // (kể cả khẩu vị người dùng vừa trả lời) và không gọi thêm LLM.
                    if (context.TripDetailsMissing)
                    {
                        if (AgentIntakePolicy.PreferencesDue(context)) return PreferencesTurn(context);
                        telemetry.Record("trip_details_clarification", context.ConversationId, context.TurnId, context.AttemptId);
                        return TripDetailsTurn(context);
                    }
                    // Hội thoại 94 tốn thêm 2 lần gọi LLM vì model xin tiếp ứng viên dù đã đủ món cho mọi bữa.
                    if (AgentIntakePolicy.PlanCovered(context)) context.ToolsClosed = true;
                    // Một tool call hợp lệ chứng tỏ output trước đã được sửa; không gửi lại thông báo "output không hợp lệ"
                    // ở các lần gọi sau, vì thông báo cũ khiến model lặp lại đúng tool call vừa chạy.
                    repaired = false;
                    // Lịch dài: model sẽ phải viết hàng trăm món và vượt giới hạn một lần trả lời, nên backend tự ghép từ các món vừa được cấp và không gọi LLM thêm.
                    if (!AgentProposalBuilder.AssemblesWholeTrip(context) || AgentProposalBuilder.Assemble(context, longTrip: true) is not { } longPlan) continue;
                    context.Grants.AddRange(context.ReserveGrants);
                    context.ReserveGrants.Clear();
                    telemetry.Record("long_trip_assembled", context.ConversationId, context.TurnId, context.AttemptId, new { days = context.State.NumDays, items = longPlan.Items.Length });
                    step = new([], longPlan, step.Tokens);
                }
                // STEP-24: Prepare output.
                var output = step.Output!;
                // STEP-25: When output.Reply is missing, output.Reply.Length > limits.MaxMessageLength, (output.Clarification exists and more.
                // Action do LLM chọn theo Instructions trong adapter; hợp đồng chỉ cho phép các action
                // được khai báo ở AgentLlmSchema.Output, đồng thời kiểm tra lại allowlist tại đây.
                // Đây là quy tắc xử lý của ANGI, không có điểm số confidence/threshold phân loại intent riêng.
                // Reply rỗng không phải lỗi: backend có câu cố định cho mọi action. Model hay để rỗng với out_of_scope vì câu từ chối là của backend;
                // từng coi đó là output hỏng khiến "hướng dẫn tôi" tốn 2-4 lần gọi LLM rồi trả 503 cho người dùng.
                if (output.Reply is null || output.Reply.Length > limits.MaxMessageLength ||
                    output.Clarification is not null && !AgentReplyBuilder.Clarifications.Contains(output.Clarification) ||
                    output.Items is null || output.Changes is null || output.Scope is null ||
                    output.Action is not ("clarify" or "answer" or "create_proposal" or "patch_proposal" or "discard_proposal" or "reset_conversation" or "request_save" or "use_tool" or "chat" or "food_info" or "out_of_scope"))
                {
                    // Chỉ ghi tên trường và độ dài, không ghi nội dung model trả về.
                    telemetry.Record("invalid_output", context.ConversationId, context.TurnId, context.AttemptId,
                        new { output.Action, output.Clarification, replyLength = output.Reply?.Length, schemaRepaired });
                    if (schemaRepaired) return Misunderstood(context, turnState);
                    schemaRepaired = repaired = sameRead = true;
                    continue;
                }
                // Chỉ ghi lựa chọn và độ dài, không ghi nội dung: để biết model đã đọc ra gì khi một lượt đi sai.
                telemetry.Record("model_output", context.ConversationId, context.TurnId, context.AttemptId, new { phase = phase.ToString(), output.Action, output.Clarification, output.Tool, output.PendingAnswer, replyLength = output.Reply.Length });
                // Model đọc xong và cần một tool: lần gọi sau chỉ có đúng tool đó, nên không thể chọn nhầm. Các kiểm tra của từng tool vẫn chạy như cũ.
                if (output.Action == "use_tool")
                {
                    if (phase != AgentTurnPhase.Read || output.Tool is null || !AgentLlmSchema.Tools.ContainsKey(output.Tool))
                    {
                        telemetry.Record("invalid_output", context.ConversationId, context.TurnId, context.AttemptId, new { output.Action, output.Tool, phase = phase.ToString() });
                        return Misunderstood(context, turnState);
                    }
                    telemetry.Record("use_tool", context.ConversationId, context.TurnId, context.AttemptId, new { output.Tool });
                    phase = AgentTurnPhase.Forced;
                    context.ForcedTool = output.Tool;
                    repaired = false;
                    continue;
                }
                // Model đọc ra câu trả lời cho câu hỏi đang chờ, hoặc ý muốn lưu lịch: dừng ở đây, việc thực hiện và kiểm tra quyền là của use case. State đầu lượt giữ nguyên.
                if (AgentConfirmationPolicy.ReadAnswer(context.AwaitedAnswer, output) is { } answer)
                {
                    telemetry.Record("pending_answer", context.ConversationId, context.TurnId, context.AttemptId, new { awaited = context.AwaitedAnswer, answer.Kind });
                    return new(output.Reply, turnState, null, null, Answer: answer);
                }
                // Đồng ý mà không có câu hỏi nào đang chờ: lối đi an toàn là hỏi lại về việc lưu, vì bước đó chưa ghi gì; câu model viết có thể đã nói như thể việc đã xong ("Chốt nhé!").
                // Khi một câu hỏi làm rõ đang chờ, "đồng ý" là trả lời câu đó ("giữ nguyên" cho "muốn đổi món nào?"), không phải ý muốn lưu: câu model viết được hiện như thường.
                if (output.Action == "request_save" || output is { Action: "answer", PendingAnswer: AgentConfirmationPolicy.Accept, State: null, Items.Length: 0, Changes.Length: 0 }
                    && context.AwaitedAnswer is null && context.CurrentProposal?.Status == "ready")
                {
                    telemetry.Record("request_save", context.ConversationId, context.TurnId, context.AttemptId);
                    return new(output.Reply, turnState, null, null, "request_save");
                }
                // Câu model viết không qua kiểm tra: cho viết lại một lần, nói rõ lỗi, thay vì đưa ngay câu cố định, vốn không đáp lại điều người dùng vừa nói.
                // Chỉ kiểm ở đây khi lượt chưa nhận gì từ model; câu hỏi làm rõ đi tiếp và nếu sai thì dùng câu cố định, vì backend có thể còn thay nó bằng câu hỏi theo thứ tự.
                // Câu trả lời kèm thay đổi state hay món còn qua các bước của backend có thể thay câu, nên chỉ viết lại câu trả lời thuần.
                // Chỉ ở bước đọc chưa gọi tool: lời gọi lại rẻ; sau khi đã gọi tool, viết lại nghĩa là lặp cả lượt đắt, nên giữ câu cố định như trước.
                if (!replyRepaired && phase == AgentTurnPhase.Read && context.Exchanges.Count == 0 && calls + 1 < limits.MaxLlmCalls &&
                    (output.Action is "chat" or "food_info" || output is { Action: "answer", State: null, Items.Length: 0, Changes.Length: 0 }))
                {
                    if (output.Action == "answer") await LoadDishWordsAsync(context, timeout.Token);
                    var issue = output.Action switch
                    {
                        "answer" => AgentReplyGuard.ChooseIssue(context, output),
                        "chat" => AgentReplyGuard.ChatIssue(output.Reply, context.Request.Content, turnState),
                        _ => AgentReplyGuard.FoodInfoIssue(output.Reply)
                    };
                    if (AgentReplyGuard.RepairHint(issue) is { } hint)
                    {
                        telemetry.Record("reply_repair", context.ConversationId, context.TurnId, context.AttemptId, new { output.Action, issue, output.PendingAnswer, awaited = context.AwaitedAnswer });
                        // Câu trả lời rỗng thường do đọc ra một câu trả lời cho câu hỏi không hề có, nên nhắc rõ là không có gì đang chờ.
                        context.RepairFeedback = output.Action == "answer" && issue == "empty"
                            ? "The reply was empty and no question is waiting for this answer. Reply naturally to the user's latest message in a short sentence, with pendingAnswer=null, changing nothing."
                            : $"Your reply could not be shown because {hint}. Keep the same action and write the reply again, naturally, answering the user's latest message.";
                        replyRepaired = repaired = sameRead = true;
                        continue;
                    }
                }
                // STEP-26: When output.Action is one of the listed values, call telemetry.Record, prepare social from AgentReplyGuard.OutOfScopeReply and 1 more action(s).
                // Dừng trước khi nhận state của model: câu lạc đề không được đổi điều kiện chuyến đi, và mọi items/changes kèm theo đều bị bỏ qua.
                if (output.Action is "chat" or "food_info" or "out_of_scope")
                {
                    telemetry.Record(output.Action, context.ConversationId, context.TurnId, context.AttemptId);
                    var social = output.Action switch
                    {
                        "chat" => AgentReplyGuard.ChatReply(output.Reply, context.Request.Content, turnState),
                        "food_info" => AgentReplyGuard.FoodInfoReply(output.Reply, context.Request.Content, output.SuggestedDish),
                        _ => AgentReplyGuard.OutOfScopeReply(context.Request.Content, output.SuggestedDish, turnState, context.TurnId)
                    };
                    // Câu từ chối vốn luôn là của backend nên không tính là bị thay.
                    if (output.Action != "out_of_scope") RecordReplaced(context, output, social, output.Action == "chat"
                        ? AgentReplyGuard.ChatIssue(output.Reply, context.Request.Content, turnState) : AgentReplyGuard.FoodInfoIssue(output.Reply));
                    // Câu từ chối nhắc lại câu hỏi nên câu hỏi vẫn mở. Với câu model tự viết, người dùng đã nói sang chuyện khác: câu hỏi cũ không còn treo,
                    // nó vẫn nằm trong lịch sử model đọc và model tự quyết có hỏi lại không.
                    return new(social, output.Action == "out_of_scope" ? turnState : turnState with { PendingQuestion = null }, null, null, output.Action);
                }
                // STEP-27: Return the new result early when output.Action is one of the listed values.
                // Model có thể bổ sung/siết điều kiện, nhưng không được tự nới ràng buộc đã chốt.
                // Cờ ConstraintsChangeConfirmed không cấp quyền; backend giữ pending change để người dùng xác nhận.
                if (output.Action is "discard_proposal" or "reset_conversation")
                {
                    if (output.Items.Length != 0 || output.Changes.Length != 0 || output.State is not null) return Misunderstood(context, turnState);
                    return new(output.Action == "discard_proposal" ? "Mình đã bỏ bản nháp này rồi. Lịch đã lưu vẫn được giữ nguyên."
                        : "Mình bắt đầu lại từ đầu nhé. Lịch đã lưu vẫn được giữ nguyên.", context.State, null, null,
                        output.Action == "discard_proposal" ? "discard" : "reset");
                }
                // STEP-28: When context.ReplanAfterConfirmation is false, set context.State.
                // Mức khẩu vị người dùng chưa nói không được vào lịch. Model đang hỏi lại thì chỉ bỏ con số đoán; còn đề xuất thì cho sửa một lần, rồi hỏi người dùng.
                if (AgentConstraintGuard.UnbackedTastes(context, output.State) is { Length: > 0 } unbackedTastes)
                {
                    telemetry.Record("taste_level_unbacked", context.ConversationId, context.TurnId, context.AttemptId, new { output.Action, count = unbackedTastes.Length });
                    if (output.Action == "clarify")
                        output = output with { State = output.State! with { TasteTargets = context.State.TasteTargets, TasteOverrides = context.State.TasteOverrides } };
                    else if (!tasteRepaired && calls + 1 < limits.MaxLlmCalls)
                    {
                        context.RejectedOutput = output;
                        context.RepairFeedback = "Mức khẩu vị phải kèm levelQuote là nguyên văn người dùng đã viết. Nếu họ chưa nêu mức, hãy hỏi lại bằng clarification=taste_intensity, không lập lịch.";
                        tasteRepaired = repaired = true;
                        continue;
                    }
                    else
                    {
                        var levelQuestion = AgentReplyBuilder.Clarify(context, "taste_intensity");
                        return new(levelQuestion, context.State with { PendingQuestion = levelQuestion }, null, null);
                    }
                }
                // Ngân sách mới phải kèm nguyên văn người dùng và đúng cỡ số đó. Cho model một lần sửa; vẫn không được thì giữ ngân sách cũ, không để một con số không có căn cứ vào chuyến đi.
                if (AgentConstraintGuard.HasUnbackedBudget(context, output.State))
                {
                    telemetry.Record("budget_unbacked", context.ConversationId, context.TurnId, context.AttemptId, new { output.Action, budgetQuoteRepaired });
                    if (!budgetQuoteRepaired && calls + 1 < limits.MaxLlmCalls)
                    {
                        context.RejectedOutput = output;
                        context.RepairFeedback = "budgetAmount phải kèm budgetQuote là nguyên văn người dùng đã viết và đúng cỡ số tiền họ nêu (số tiền theo ngày thì nhân với số ngày). Nếu họ chưa nêu ngân sách, giữ nguyên giá trị cũ.";
                        budgetQuoteRepaired = repaired = true;
                        continue;
                    }
                    output = output with { State = output.State! with { BudgetAmount = context.State.BudgetAmount, BudgetUnrestricted = context.State.BudgetUnrestricted } };
                }
                // Model chỉ hỏi lại và không đề xuất state nào: chưa có gì để áp, nên chưa hỏi xác nhận các con số đang giữ, tránh chen ngang câu hỏi của model.
                if (!context.ReplanAfterConfirmation && !(output.Action == "clarify" && output.State is null))
                    context.State = statePolicy.Accept(context.State, AgentConstraintGuard.RetainPlacements(
                        AgentMealScopePolicy.Narrow(AgentConstraintGuard.WithoutErased(AgentIntakePolicy.WithCatalogProvince(AgentIntakePolicy.WithoutUnstatedProvince(output.State, context), context), context.State, context.Request.Content), context.State, AgentMealScopePolicy.Evidence(context)), context.PlacedRequests), context.ConstraintBaseline);
                // STEP-29: Handle the early exits in order: an unknown province, the preferences question, then the new result.
                // Tỉnh ANGI chưa có dữ liệu: nói rõ rồi hỏi lại, chữ của model có thể đã coi nơi đó là được.
                if (context.UnknownPlace is not null) return TripDetailsTurn(context);
                // Chuyến đi đã đủ thông tin mà model định lập lịch hoặc hỏi lại chuyện đã biết: câu hỏi khẩu vị vẫn phải đến trước, nên không bước nào bị bỏ qua.
                if (AgentIntakePolicy.PreferencesDue(context) && output.Action != "answer" && output.Clarification is null or "general" or "trip_details") return PreferencesTurn(context);
                // Người dùng chỉ nói sở thích ("thích ăn cay") mà model tự đổi món và không ghi mức nào vào state: chưa ai nêu mức thì lịch chưa được đổi.
                // Cho model một lần sửa để tự hỏi bằng lời của nó; vẫn không được thì dùng câu hỏi cố định. Lịch hiện tại giữ nguyên.
                if (output.Action is "create_proposal" or "patch_proposal" && context.Source is { Items.Length: > 0 } && !context.ReplanAfterConfirmation && !context.ItineraryChanged &&
                    AgentConstraintGuard.ActsOnUnstatedLevel(context.Request.Content, confirmedState, context.State))
                {
                    telemetry.Record("taste_level_missing", context.ConversationId, context.TurnId, context.AttemptId, new { output.Action, tasteRepaired });
                    context.State = confirmedState;
                    if (!tasteRepaired && calls + 1 < limits.MaxLlmCalls)
                    {
                        context.RejectedOutput = output;
                        context.RepairFeedback = "Người dùng nhắc khẩu vị nhưng state chưa ghi mức. Nếu họ đã nêu mức, ghi vào tasteTargets hoặc tasteOverrides kèm levelQuote. Nếu chưa, hãy hỏi lại bằng clarification=taste_intensity, không đổi món.";
                        tasteRepaired = repaired = true;
                        continue;
                    }
                    var missingLevel = AgentReplyBuilder.Clarify(context, "taste_intensity");
                    return new(missingLevel, confirmedState with { PendingQuestion = missingLevel }, null, null);
                }
                // STEP-30: When output.Action is "clarify" or "answer", check whether output.Items.Length is not 0 or output.Changes.Length is not 0 and more.
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
                    if (context.CatalogNotice is not null) context.State = AgentConstraintGuard.WithoutUnsettledNames(context.State, confirmedState);
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
                        var checkedFuture = await validator.ValidateAsync(future, futureSource, false, Routes(context), timeout.Token, context.Grants);
                        if (checkedFuture.Validation.Errors.Length > 0)
                            return new("Chưa thể giữ nguyên đề xuất theo các điều kiện mới. Vui lòng kiểm tra lại yêu cầu.", context.State, null, checkedFuture.Validation);
                        return new("Đã cập nhật khẩu vị cho các gợi ý tiếp theo. Các món trong bản đề xuất hiện tại được giữ nguyên; lịch chính thức chưa thay đổi.",
                            future.State, checkedFuture.Snapshot with { Notes = AgentReplyBuilder.PlanNotes(checkedFuture.Snapshot with { State = future.State }) }, checkedFuture.Validation);
                    }
                    // Các câu hỏi trước khi lập lịch đi theo thứ tự: chưa rõ địa điểm, số ngày, ngân sách thì chưa hỏi phạm vi hay khẩu vị.
                    if (output.Action == "clarify" && AgentIntakePolicy.Eligible(context) && AgentReplyBuilder.TripDetailsQuestion(context.State) is not null &&
                        (output.Clarification == "preferences" || output.Clarification == "place_scope" && string.IsNullOrWhiteSpace(context.State.Province)))
                        return TripDetailsTurn(context);
                    await LoadDishWordsAsync(context, timeout.Token);
                    var reply = AgentReplyGuard.Choose(context, output);
                    RecordReplaced(context, output, reply, AgentReplyGuard.ChooseIssue(context, output));
                    // Model đã trả lời thay vì hỏi tiếp nghĩa là câu hỏi cũ không còn mở; câu hỏi khẩu vị còn gắn với yêu cầu đang lập nên giữ.
                    context.State = context.State with { PendingQuestion = output.Action == "clarify" ? reply[..Math.Min(reply.Length, 1000)] : null };
                    // Ghi lại đang hỏi tới đâu và yêu cầu đang lập, để lượt sau backend biết thứ tự và model đọc được cả yêu cầu.
                    if (output.Action == "clarify" && !AgentIntakePolicy.HasPlan(context) && output.Clarification is "trip_details" or "place_scope" or "preferences")
                    {
                        var stage = output.Clarification switch { "place_scope" => AgentIntakePolicy.Scope, "preferences" => AgentIntakePolicy.Preferences, _ => AgentIntakePolicy.Details };
                        context.State = context.State with { PlanningIntake = stage, PendingPlanningRequest = context.State.PendingPlanningRequest ?? AgentIntakePolicy.CaptureRequest(null, context.Request.Content) };
                        telemetry.Record("intake", context.ConversationId, context.TurnId, context.AttemptId, new { stage });
                    }
                    // Nơi backend vừa định vị được nói kèm câu hỏi: người dùng thấy ngay nếu bị hiểu sai trước khi có món nào được chọn.
                    if (output.Action == "clarify") reply = WithPlaceNote(context, reply);
                    return new(reply, context.State, null, null);
                }
                // STEP-31: Update context: State.
                // create_proposal supplies a scoped selection; backend creates or updates from the owned source.
                // patch_proposal supplies explicit add/replace/remove/move operations on that source.
                // Builder kiểm tra nguồn, scope và baseProposalVersion; Validator kiểm tra catalog,
                // nguồn candidate, giá, ngân sách và tuyến. Action hợp lệ chưa có nghĩa proposal hợp lệ.
                context.State = context.State with { PendingQuestion = null, PendingPlanningRequest = null };
                // STEP-32: Call telemetry.Record.
                telemetry.Record("proposal_output", context.ConversationId, context.TurnId, context.AttemptId,
                    new { output.Action, output.Scope, output.BaseProposalVersion, itemCount = output.Items.Length,
                        changeCount = output.Changes.Length, sourceItemCount = context.Source?.Items.Length });
                // STEP-33: Prepare snapshot.
                AgentSnapshot snapshot;
                // STEP-34: Set snapshot.
                try { snapshot = builder.Build(context, output); }
                catch (ConflictException error) when (error.ErrorCode == "PROPOSAL_NOT_COMMITTABLE" &&
                    output.Items.Length == 0 && output.Changes.Length == 0 && AgentReplyBuilder.OverBudgetOnly(lastValidation))
                {
                    // STEP-35: On ConflictException when error.ErrorCode is "PROPOSAL_NOT_COMMITTABLE", output.Items.Length is 0, output.Changes.Length is 0 and 1 more condition(s) and more.
                    // Model bỏ cuộc (đề xuất rỗng) sau khi lịch chỉ bị chặn vì vượt ngân sách: hỏi người dùng thay vì báo lỗi chung.
                    telemetry.Record("budget_clarification", context.ConversationId, context.TurnId, context.AttemptId, new { lastValidation!.TotalCost });
                    return BudgetQuestion(context.State, lastValidation);
                }
                catch (ConflictException error) when (error.ErrorCode == "PROPOSAL_NOT_COMMITTABLE" &&
                    output.Items.Length == 0 && output.Changes.Length == 0)
                {
                    // STEP-36: On ConflictException when error.ErrorCode is "PROPOSAL_NOT_COMMITTABLE", output.Items.Length is 0 and output.Changes.Length is 0 and more.
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
                    // STEP-37: On ConflictException when error.ErrorCode is "PROPOSAL_NOT_COMMITTABLE", proposalRepaired is false and calls + 1 < limits.MaxLlmCalls and more.
                    telemetry.Record("proposal_repair", context.ConversationId, context.TurnId, context.AttemptId,
                        new { output.Action, output.Scope, error.ErrorCode, reason = error.Message });
                    context.RepairFeedback = error.Message;
                    context.RejectedOutput = output;
                    proposalRepaired = repaired = true;
                    continue;
                }
                catch (ConflictException error) when (error.ErrorCode == "PROPOSAL_NOT_COMMITTABLE")
                {
                    // STEP-38: On ConflictException when error.ErrorCode is "PROPOSAL_NOT_COMMITTABLE", call telemetry.Record and more.
                    // Model vẫn gửi đề xuất sai sau lượt sửa: lỗi của model, không phải của người dùng, và không được thành lỗi 409 "bản nháp không đủ điều kiện".
                    // Lịch mới thì backend tự ghép từ các món đã được cấp, không tốn thêm lần gọi LLM; còn lại giữ nguyên bản đang có.
                    telemetry.Record("proposal_unusable", context.ConversationId, context.TurnId, context.AttemptId,
                        new { output.Action, output.Scope, reason = error.Message });
                    if (AgentProposalBuilder.Assemble(context) is not { } assembled)
                        return new(AgentReplyBuilder.PlanningIncomplete, confirmedState with { PendingQuestion = null }, null, lastValidation);
                    output = assembled;
                    snapshot = builder.Build(context, output);
                }
                // STEP-39: When context.Source exists and output.Changes.Any matches the filter, set context.State and set snapshot.
                // Model xóa một bữa theo yêu cầu người dùng: bữa đó được phép trống, nếu không luật đủ bữa sẽ từ chối đúng điều người dùng vừa yêu cầu.
                if (context.Source is { } removalSource && output.Changes.Any(change => change.Type == "remove"))
                {
                    context.State = AgentConstraintGuard.WithSkippedMeals(context.State, removalSource, snapshot.Items);
                    snapshot = snapshot with { State = snapshot.State with { SkippedMeals = context.State.SkippedMeals } };
                }
                // STEP-40: Run validator.ValidateAsync and keep validated.
                var validated = await validator.ValidateAsync(snapshot, context.Source, false, Routes(context), timeout.Token, context.Grants, context.ReserveGrants);
                // STEP-41: Call telemetry.Record.
                telemetry.Record("validation", context.ConversationId, context.TurnId, context.AttemptId, new { validated.Validation.Errors, validated.Validation.Warnings });
                // STEP-43: When Validation.ScopeRejectedItemKeys has the expected shape and validator.RepairScopeAsync() exists and more.
                // Lỗi DUY NHẤT là món nằm ngoài ngày/bữa được cấp: backend thay bằng món xếp hạng cao nhất đã được cấp cho
                // đúng vị trí, cùng khẩu vị/yêu cầu món, không trùng món và trong ngân sách, rồi kiểm tra lại toàn bộ.
                // Không tốn thêm lần gọi LLM; nếu không có món thay an toàn thì giữ luồng model tự sửa như cũ.
                if (validated.Validation.ScopeRejectedItemKeys is { Length: > 0 } rejectedKeys &&
                    await validator.RepairScopeAsync(snapshot, context.Source, context.Grants, rejectedKeys, timeout.Token) is { } repairedSnapshot)
                {
                    var revalidated = await validator.ValidateAsync(repairedSnapshot, context.Source, false, Routes(context), timeout.Token, context.Grants, context.ReserveGrants);
                    telemetry.Record("scope_autorepair", context.ConversationId, context.TurnId, context.AttemptId,
                        new { replaced = rejectedKeys.Length, revalidated.Validation.Errors });
                    if (revalidated.Validation.Errors.Length == 0) validated = revalidated;
                }
                // STEP-44: Prepare repeatGrants.
                // Grant dự phòng đứng trước grant chính vì RepairScopeAsync ưu tiên grant cấp sau: món model đã thấy vẫn được chọn trước.
                List<AgentCandidateGrant> repeatGrants = [.. context.ReserveGrants, .. context.Grants];
                // STEP-45: When AgentReplyBuilder.OverBudgetOnly(validated.Validation) holds and validator.RepairBudgetAsync() exists and more.
                // Chỉ vượt ngân sách: backend đổi các món đắt nhất sang món rẻ hơn đã xếp hạng cho đúng vị trí, không hỏi người dùng nới
                // khi catalog vẫn còn món rẻ và không tốn thêm lượt gọi LLM. Hết món rẻ hơn thì mới hỏi như cũ.
                if (AgentReplyBuilder.OverBudgetOnly(validated.Validation) &&
                    await validator.RepairBudgetAsync(validated.Snapshot, context.Source, [.. context.Grants, .. context.ReserveGrants], timeout.Token) is { } cheaper)
                {
                    var revalidated = await validator.ValidateAsync(cheaper.Snapshot, context.Source, false, Routes(context), timeout.Token, repeatGrants);
                    telemetry.Record("budget_autorepair", context.ConversationId, context.TurnId, context.AttemptId,
                        new { replaced = cheaper.Replaced, revalidated.Validation.TotalCost, revalidated.Validation.Errors });
                    if (revalidated.Validation.Errors.Length == 0) validated = revalidated;
                }
                // STEP-46: When validated.Validation.Errors.Length is 0, Validation.AvoidableRepeatItemKeys has the expected shape and validator.RepairScopeAsync() exists and more.
                // Món lặp tránh được (còn món chưa dùng đã cấp cho đúng vị trí): backend tự thay, không tốn thêm lượt gọi LLM.
                // Không thay được an toàn thì giữ nguyên lịch hợp lệ; lặp chỉ là ưu tiên đa dạng, không chặn đề xuất.
                if (validated.Validation.Errors.Length == 0 && validated.Validation.AvoidableRepeatItemKeys is { Length: > 0 } repeatKeys &&
                    await validator.RepairScopeAsync(validated.Snapshot, context.Source, repeatGrants, repeatKeys, timeout.Token, repeatsOnly: true) is { } varied)
                {
                    var revalidated = await validator.ValidateAsync(varied, context.Source, false, Routes(context), timeout.Token, repeatGrants);
                    telemetry.Record("repeat_autorepair", context.ConversationId, context.TurnId, context.AttemptId,
                        new { replaced = repeatKeys.Length, revalidated.Validation.Errors });
                    if (revalidated.Validation.Errors.Length == 0) validated = revalidated;
                }
                // STEP-47: When validated.Validation.Errors.Length > 0, set lastValidation, skip this round when proposalRepaired is false and calls + 1 < limits.MaxLlmCalls and 2 more action(s).
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
                // STEP-48: Update context: PreviewOutput.
                // Mới là kết quả chuẩn bị. SendAgentMessageUseCase còn kiểm tra lease rồi mới lưu proposal;
                // chỉ writer chung của confirm/chat-save mới áp dụng proposal sau xác nhận rõ ràng.
                context.PreviewOutput = output;
                // STEP-49: Prepare reviewedState from AgentConstraintGuard.Uncovered.
                var reviewedState = context.State with { FocusDay = null, FocusMeal = null, PlanningIntake = null,
                    ReplacedDishNames = AgentScopedPlanning.Replaced(context.State, context.Source, validated.Snapshot),
                    SkippedMeals = AgentConstraintGuard.Uncovered(context.State.SkippedMeals ?? [], validated.Snapshot.Items) };
                // STEP-50: Return the new result.
                await LoadDishWordsAsync(context, timeout.Token);
                var wording = AgentReplyBuilder.WordedSummary(output.Reply, validated.Snapshot, context.Source, context);
                if (wording is null) RecordReplaced(context, output, "", AgentReplyBuilder.WordedSummaryIssue(output.Reply, validated.Snapshot, context.Source, context));
                else if (AgentReplyBuilder.MissingSlots(output.Reply, validated.Snapshot, context.Source) is { Length: > 0 } missingSlots)
                    telemetry.Record("reply_completed", context.ConversationId, context.TurnId, context.AttemptId, new { output.Action, missingSlots });
                var reviewed = validated.Snapshot with { State = reviewedState };
                return new(AgentReplyBuilder.Proposal(validated.Snapshot, context.Source, AgentReplyGuard.Opener(output.Reply), wording), reviewedState,
                    reviewed with { Notes = AgentReplyBuilder.PlanNotes(reviewed) }, validated.Validation);
            }
            // STEP-51: Throw the error from Unavailable.
            // Đã dùng hết số lần gọi cho phép mà chưa có kết quả cuối hợp lệ.
            throw Unavailable();
        }
        catch (AgentStateChangeRequiredException change)
        // STEP-52: On AgentStateChangeRequiredException, return the result of AgentConfirmationPolicy.StageChange.
        { return AgentConfirmationPolicy.StageChange(confirmedState, change.Proposed, context.Source is not null || context.Roadmap is not null); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && context.DishSuggestion is not null)
        // STEP-53: On OperationCanceledException when ct.IsCancellationRequested is false and context.DishSuggestion exists, return the result of SuggestionTurnAsync.
        { return await SuggestionTurnAsync(context, context.DishSuggestion, null, turnState, ct); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        // STEP-54: On OperationCanceledException when ct.IsCancellationRequested is false, throw AgentProviderException.
        { throw new AgentProviderException(new(AgentLlmFailureKind.TurnTimeout)); }
        // State do model đề xuất sai luật: lỗi của model, không phải của người dùng. Giữ state đầu lượt và hỏi lại.
        // STEP-55: On FluentValidation.ValidationException for a new trip, return the validator's message.
        // Người dùng vừa nêu một giá trị không hợp lệ ("145 ngày"): nói rõ giới hạn, không hỏi lại như chưa nghe gì.
        catch (FluentValidation.ValidationException error) when (context.CatalogProvinces is not null && AgentIntakePolicy.Eligible(context))
        {
            var problem = error.Errors.FirstOrDefault()?.ErrorMessage ?? "Thông tin chuyến đi chưa hợp lệ.";
            return new($"{problem} Bạn nhắn lại giúp mình nhé.", turnState with { PlanningIntake = AgentIntakePolicy.Details }, null, null);
        }
        // STEP-56: On any other FluentValidation.ValidationException, return the result of Misunderstood.
        catch (FluentValidation.ValidationException) { return Misunderstood(context, turnState); }
    }

    /// <summary>Route legs are fetched per day from a paid map service for each validation; a long trip skips them in chat and the saved roadmap fetches a day's route when it is opened.</summary>
    private static bool Routes(AgentContext context) => !AgentScopedPlanning.IsLongTrip(context.State);

    /// <summary>
    /// A point pinned on the map already says where the user will eat, so a new plan never asks again: it gives the province and the search circle.
    /// Only a state with no province takes these, so a user who later widened the search is not narrowed again.
    /// </summary>
    private async Task UsePinnedAreaAsync(AgentContext context, CancellationToken ct)
    {
        // STEP-01: Prepare state.
        var state = context.State;
        // STEP-02: Stop here when places is missing, state.OriginMode is not "pinned", state.OriginLatitude is not { } latitude or 4 more condition(s).
        if (places is null || state.OriginMode != "pinned" || state.OriginLatitude is not { } latitude || state.OriginLongitude is not { } longitude ||
            !string.IsNullOrWhiteSpace(state.Province) || state.PendingStateChange is not null || AgentIntakePolicy.HasPlan(context)) return;
        // STEP-03: Stop here when places.AroundPointAsync() is not { } area.
        if (await places.AroundPointAsync(latitude, longitude, ct) is not { } area)
        {
            context.PlaceNote = "Quanh điểm bạn chấm trên bản đồ chưa có quán nào trên ANGI.";
            return;
        }
        // STEP-04: Update context: State and PlaceNote.
        context.State = state with { Province = area.Province, RadiusKm = state.RadiusKm ?? area.RadiusKm };
        context.PlaceNote = $"Mình sẽ tìm quán trong khoảng {context.State.RadiusKm:0} km quanh điểm bạn đã chấm trên bản đồ.";
        // STEP-05: Call telemetry.Record.
        telemetry.Record("pinned_area", context.ConversationId, context.TurnId, context.AttemptId, new { context.State.RadiusKm });
    }

    /// <summary>
    /// Prepares a new trip's turn from what carries no language: shared coordinates become a province, and a point with no restaurant around it gets its fixed answer.
    /// The message itself is the model's to read; null means the turn goes on to it.
    /// </summary>
    private async Task<AgentPreparedTurn?> StartIntakeAsync(AgentContext context, CancellationToken ct)
    {
        // STEP-01: Return null early when catalog is missing or AgentIntakePolicy.Eligible(context) does not hold.
        if (catalog is null || !AgentIntakePolicy.Eligible(context)) return null;
        // STEP-02: Load provinces through catalog.GetProvincesAsync.
        var provinces = context.CatalogProvinces = await catalog.GetProvincesAsync(ct);
        // STEP-03: When the turn shares coordinates, set the nearest province, or return the fixed answer and drop them when none is near.
        if (AgentIntakePolicy.NeedsNearby(context))
        {
            var state = context.State;
            if (await catalog.NearestProvinceAsync(state.OriginLatitude!.Value, state.OriginLongitude!.Value, AgentIntakePolicy.MaxNearbyKm, ct) is not { } nearby)
                return new(AgentIntakePolicy.NoRestaurantNearby("Quanh vị trí hiện tại của bạn chưa có quán nào trên ANGI.", provinces),
                    state with { OriginMode = "none", OriginLatitude = null, OriginLongitude = null, OriginLabel = null, RadiusKm = null, PlanningIntake = AgentIntakePolicy.Details, PendingQuestion = AgentIntakePolicy.PlaceQuestion }, null, null);
            context.State = state with { Province = nearby, RadiusKm = AgentIntakePolicy.DefaultRadiusKm };
            context.PlaceNote = $"Bạn đang ở gần {AgentReplyBuilder.Safe(nearby)}, mình sẽ ưu tiên quán trong khoảng {AgentIntakePolicy.DefaultRadiusKm:0} km quanh bạn.";
        }
        // STEP-04: Return the fixed answer early when a pinned point has no restaurant around it.
        if (context.PlaceNote is { } note && string.IsNullOrWhiteSpace(context.State.Province))
            return new(AgentIntakePolicy.NoRestaurantNearby(note, provinces), context.State with { PlanningIntake = AgentIntakePolicy.Details, PendingQuestion = AgentIntakePolicy.PlaceQuestion }, null, null);
        // STEP-05: Update context: IntakeComplete, then return null.
        context.IntakeComplete = context.State.PlanningIntake == AgentIntakePolicy.Preferences;
        return null;
    }

    /// <summary>Asks the preferences question in fixed wording when the model tried to go past it; no further provider call.</summary>
    private AgentPreparedTurn PreferencesTurn(AgentContext context)
    {
        // STEP-01: Prepare the question, then call telemetry.Record.
        var (reply, state) = AgentIntakePolicy.AskPreferences(context.State);
        telemetry.Record("intake", context.ConversationId, context.TurnId, context.AttemptId, new { stage = state.PlanningIntake });
        // STEP-02: Return the new result, keeping the request being planned.
        return new(WithPlaceNote(context, reply), state with { PendingPlanningRequest = state.PendingPlanningRequest ?? AgentIntakePolicy.CaptureRequest(null, context.Request.Content) }, null, null);
    }

    /// <summary>Asks the place, duration or budget still missing in fixed wording, and says so first when the place named has no data; no further provider call.</summary>
    private static AgentPreparedTurn TripDetailsTurn(AgentContext context)
    {
        // STEP-01: Prepare question from an unknown province or the details still missing.
        var question = context.UnknownPlace is { } place && context.CatalogProvinces is { } provinces
            ? AgentIntakePolicy.UnknownPlaceReply(place, provinces) : AgentReplyBuilder.TripDetailsQuestion(context.State) ?? AgentIntakePolicy.PlaceQuestion;
        // STEP-02: Return the new result.
        return new(WithPlaceNote(context, question), context.State with { PendingQuestion = question[..Math.Min(question.Length, 1000)], PlanningIntake = AgentIntakePolicy.HasPlan(context) ? null : AgentIntakePolicy.Details,
            PendingPlanningRequest = AgentIntakePolicy.HasPlan(context) ? context.State.PendingPlanningRequest : context.State.PendingPlanningRequest ?? AgentIntakePolicy.CaptureRequest(null, context.Request.Content) }, null, null);
    }

    private static string WithPlaceNote(AgentContext context, string reply) => context.PlaceNote is { } note ? $"{note} {reply}" : reply;

    /// <summary>Logs that a check threw away the model's wording, never the wording itself, so reviews can see how often the checks block it.</summary>
    private void RecordReplaced(AgentContext context, AgentOutput output, string shown, string? reason)
    {
        // Không có lý do nghĩa là câu của model đã qua kiểm tra; câu hiển thị chỉ khác vì backend điền ô (như {plan}), không phải bị thay.
        if (reason is not null && output.Reply.Trim() is { Length: > 0 } written && !shown.StartsWith(written, StringComparison.Ordinal))
            telemetry.Record("reply_replaced", context.ConversationId, context.TurnId, context.AttemptId, new { output.Action, output.Clarification, reason });
    }

    /// <summary>Reads the catalog's common dish words once per turn, so a reply can be checked against them.</summary>
    private async Task LoadDishWordsAsync(AgentContext context, CancellationToken ct)
    {
        if (catalog is not null && context.DishWords is null) context.DishWords = AgentReplyGuard.DishWords(await catalog.GetDishNamesAsync(ct));
    }

    /// <summary>Ends a suggest_dish turn with the model's checked wording, or the fixed reply when it wrote none; the plan and state stay as the turn began.</summary>
    private async Task<AgentPreparedTurn> SuggestionTurnAsync(AgentContext context, AgentDishSuggestion suggestion, AgentOutput? output, AgentState turnState, CancellationToken ct)
    {
        // STEP-01: When output exists, call LoadDishWordsAsync.
        if (output is not null) await LoadDishWordsAsync(context, ct);
        // STEP-02: Prepare reply through AgentDishSuggestionPolicy.Worded, call telemetry.Record and RecordReplaced when output exists.
        var reply = AgentDishSuggestionPolicy.Worded(output?.Reply, context, suggestion);
        telemetry.Record("dish_suggestion", context.ConversationId, context.TurnId, context.AttemptId, new { provider = true, suggestion.Personalized });
        // Câu hiển thị đã được điền dữ kiện nên không còn giống chữ model viết; chỉ tính là bị thay khi phải dùng câu cố định.
        if (output is { Reply: not null } && reply == suggestion.FixedReply) RecordReplaced(context, output, reply, AgentDishSuggestionPolicy.WordingIssue(output.Reply, context, suggestion));
        // STEP-03: Return the new result. The user moved on, so an earlier question no longer waits; the plan being built stays in the state and the question in the history.
        return new(reply, turnState with { DishOffered = true, PendingQuestion = null }, null, null, "chat");
    }

    /// <summary>A fixed question for output the backend cannot use; the turn state is unchanged and the user is never shown a service error for it.</summary>
    private AgentPreparedTurn Misunderstood(AgentContext context, AgentState state)
    {
        telemetry.Record("misunderstood", context.ConversationId, context.TurnId, context.AttemptId);
        return new(AgentReplyBuilder.TripDetailsQuestion(state) is { } question && !AgentIntakePolicy.HasPlan(context) ? question : AgentReplyGuard.NotUnderstood, state, null, null);
    }

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
    string? Action = null, AgentCommitPreparation? Commit = null, AgentAnswer? Answer = null);
