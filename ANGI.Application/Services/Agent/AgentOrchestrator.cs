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
        var assembled = false;
        // Đề xuất rỗng có lượt sửa riêng để không tiêu mất lượt sửa lỗi validation của lịch đủ món sau đó.
        var emptyRepaired = false;
        var coverageRepaired = false;
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
        string? lastReply = null;
        try
        {
            // Prompt mẫu là nút bấm, không phải câu người dùng gõ: backend tự chạy đúng một tool với tham số cố định (tỉnh, bữa, ngân sách, phạm vi),
            // model chỉ đọc kết quả và viết câu trả lời. Trước đây cùng một câu mẫu lúc được hỏi lại, lúc gợi ý món, lúc lập cả lịch (hội thoại 549, 550, 552, 553).
            if (await tools.StarterCallAsync(context, timeout.Token) is { } starter)
            {
                var starterResult = await tools.ExecuteAsync(context, starter, timeout.Token);
                executedTools.Add(starter.Name + ":" + starter.Arguments.GetRawText());
                context.Exchanges.Add(new([starter], [starterResult]));
                toolRounds++;
                toolCalls++;
                // Gợi ý món: lần gọi model duy nhất chỉ viết câu cho các món đã chọn.
                if (context.DishSuggestion is not null) context.ToolsClosed = true;
                telemetry.Record("starter", context.ConversationId, context.TurnId, context.AttemptId, new { context.Request.Command, starter.Name });
            }
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
                // Vượt giới hạn thì thu gọn phần model đọc trước khi bỏ cuộc; người dùng vẫn thấy lịch và lịch sử đầy đủ.
                if (ContextSize(context) > limits.MaxContextCharacters) Shrink(context);
                if (ContextSize(context) > limits.MaxContextCharacters)
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
                    elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds, step.Tokens, step.CachedTokens, callNumber = calls + 1,
                    repair = repaired, limits.MaxOutputTokens, limits.ReasoningEffort,
                    schemaRepaired, proposalRepaired, emptyRepaired, coverageRepaired,
                    failureKind = step.Failure?.Kind.ToString(), step.Failure?.HttpStatus, step.Failure?.RetryAfterSeconds,
                    step.Failure?.ProviderCode, step.Failure?.Reason
                });
                // Chỉ vào trace Development: model đã trả gì ở lần gọi này (tool nó gọi, output đã đọc, hoặc chuỗi thô khi sai schema).
                telemetry.Trace("llm_output", context.ConversationId, context.TurnId, context.AttemptId, new
                {
                    callNumber = calls + 1, repair = repaired, context.RepairFeedback,
                    tools = step.Tools.Select(tool => new { tool.Name, tool.Arguments }), step.Output, rawContent = step.RawContent
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
                    context.ReadState = null;
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
                    // THÍ NGHIỆM: thiếu thông tin chuyến đi không còn trả câu hỏi cố định; model đọc status của tool và tự hỏi.
                    // Hội thoại 94 tốn thêm 2 lần gọi LLM vì model xin tiếp ứng viên dù đã đủ món cho mọi bữa.
                    // THÍ NGHIỆM: không tự đóng tool khi backend thấy đã đủ món.
                    // Một tool call hợp lệ chứng tỏ output trước đã được sửa; không gửi lại thông báo "output không hợp lệ"
                    // ở các lần gọi sau, vì thông báo cũ khiến model lặp lại đúng tool call vừa chạy.
                    repaired = false;
                    // Lịch dài: model sẽ phải viết hàng trăm món và vượt giới hạn một lần trả lời, nên backend tự ghép từ các món vừa được cấp và không gọi LLM thêm.
                    if (!AgentProposalBuilder.AssemblesWholeTrip(context) || AgentProposalBuilder.Assemble(context, longTrip: true) is not { } longPlan) continue;
                    context.Grants.AddRange(context.ReserveGrants);
                    context.ReserveGrants.Clear();
                    telemetry.Record("long_trip_assembled", context.ConversationId, context.TurnId, context.AttemptId, new { days = context.State.NumDays, items = longPlan.Items.Length });
                    // No model call writes this turn's words, so it gets the prompt's own new-plan template; the backend fills its slots as for any draft.
                    step = new([], longPlan with { Reply = LongTripReply }, step.Tokens);
                    assembled = true;
                }
                // STEP-24: Prepare output.
                var output = step.Output!;
                lastReply = output.Reply;
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
                    if (schemaRepaired) return Misunderstood(context, turnState, output.Reply);
                    schemaRepaired = repaired = sameRead = true;
                    continue;
                }
                // Mọi câu người dùng thấy đều do model viết, backend không có câu thay thế. Câu rỗng (hội thoại 239: "chốt lịch này" lưu xong mà không có chữ nào)
                // được gửi lại cho model sửa một lần; vẫn rỗng thì báo lỗi có kiểm soát thay vì hiện tin nhắn trống.
                if (string.IsNullOrWhiteSpace(output.Reply) && output.Action != "use_tool" && !assembled)
                {
                    telemetry.Record("empty_reply", context.ConversationId, context.TurnId, context.AttemptId, new { output.Action, emptyRepaired });
                    if (emptyRepaired || calls + 1 >= limits.MaxLlmCalls) throw Unavailable();
                    context.RejectedOutput = output;
                    context.RepairFeedback = "The reply was empty. Keep the same action and data, and write the reply the user will see: one or two short Vietnamese sentences, never empty.";
                    emptyRepaired = repaired = sameRead = true;
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
                        return Misunderstood(context, turnState, output.Reply);
                    }
                    telemetry.Record("use_tool", context.ConversationId, context.TurnId, context.AttemptId, new { output.Tool });
                    phase = AgentTurnPhase.Forced;
                    context.ForcedTool = output.Tool;
                    // Lần gọi sau đọc lại cùng tin nhắn từ đầu; không có kết quả đọc này thì nó có thể đọc khác (hội thoại 558 mất 10 món người dùng chỉ "các món trên").
                    context.ReadState = output.State;
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
                if (output.Action == "request_save")
                {
                    telemetry.Record("request_save", context.ConversationId, context.TurnId, context.AttemptId);
                    return new(output.Reply, turnState, null, null, "request_save");
                }
                // THÍ NGHIỆM: bỏ bước kiểm câu và bắt model viết lại (ReplyGuard).
                // STEP-26: When output.Action is one of the listed values, call telemetry.Record and return the model reply as it is.
                // Dừng trước khi nhận state của model: câu lạc đề không được đổi điều kiện chuyến đi, và mọi items/changes kèm theo đều bị bỏ qua.
                if (output.Action is "chat" or "food_info" or "out_of_scope")
                {
                    telemetry.Record(output.Action, context.ConversationId, context.TurnId, context.AttemptId);
                    // THÍ NGHIỆM: hiện nguyên văn câu model viết, kể cả out_of_scope (không còn câu từ chối cố định, không dán câu hỏi cũ).
                    var social = Raw(output.Reply);
                    // Câu từ chối nhắc lại câu hỏi nên câu hỏi vẫn mở. Với câu model tự viết, người dùng đã nói sang chuyện khác: câu hỏi cũ không còn treo,
                    // nó vẫn nằm trong lịch sử model đọc và model tự quyết có hỏi lại không.
                    return new(social, turnState with { PendingQuestion = null }, null, null, output.Action);
                }
                // STEP-27: Return the new result early when output.Action is one of the listed values.
                // Model có thể bổ sung/siết điều kiện, nhưng không được tự nới ràng buộc đã chốt.
                // Cờ ConstraintsChangeConfirmed không cấp quyền; backend giữ pending change để người dùng xác nhận.
                if (output.Action is "discard_proposal" or "reset_conversation")
                {
                    if (output.Items.Length != 0 || output.Changes.Length != 0 || output.State is not null) return Misunderstood(context, turnState, output.Reply);
                    return new(Raw(output.Reply), context.State, null, null,
                        output.Action == "discard_proposal" ? "discard" : "reset");
                }
                // THÍ NGHIỆM: bỏ kiểm bằng chứng mức khẩu vị (levelQuote) và ngân sách (budgetQuote).
                // Model chỉ hỏi lại và không đề xuất state nào: chưa có gì để áp, nên chưa hỏi xác nhận các con số đang giữ, tránh chen ngang câu hỏi của model.
                if (!context.ReplanAfterConfirmation && !(output.Action == "clarify" && output.State is null))
                    context.State = AcceptRaw(context, output.State);
                // STEP-29: Handle the early exits in order: an unknown province, the preferences question, then the new result.
                // Tỉnh ANGI chưa có dữ liệu: nói rõ rồi hỏi lại, chữ của model có thể đã coi nơi đó là được.
                // Chuyến đi đã đủ thông tin mà model định lập lịch hoặc hỏi lại chuyện đã biết: câu hỏi khẩu vị vẫn phải đến trước, nên không bước nào bị bỏ qua.
                // THÍ NGHIỆM: không ép thứ tự hỏi (câu hỏi khẩu vị cố định).
                // THÍ NGHIỆM: bỏ regex ActsOnUnstatedLevel.
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
                    // THÍ NGHIỆM: bỏ intake_repair.
                    // Tên món/quán catalog chưa xác nhận (không thấy, nhiều kết quả, ngoài tỉnh) chưa phải ràng buộc: không giữ trong state,
                    // nếu không lượt sau người dùng chọn quán khác sẽ bị hỏi "xác nhận thay đổi yêu cầu món/quán".
                    if (context.CatalogNotice is not null) context.State = AgentConstraintGuard.WithoutUnsettledNames(context.State, confirmedState);
                    // THÍ NGHIỆM: model được tự hỏi về thiếu món hay ngân sách bằng lời của nó.
                    // THÍ NGHIỆM: không bắt model đổi answer thành đề xuất khi điều kiện ngày/bữa đổi.
                    if (output.Action == "answer" && context.State.PreferenceApplication == "future" && context.Source is { } futureSource &&
                        futureSource.Items.Any(item => AgentScopedPlanning.Fingerprint(futureSource.State, item.DayNumber, item.MealSlot) !=
                            AgentScopedPlanning.Fingerprint(context.State, item.DayNumber, item.MealSlot)))
                    {
                        var future = futureSource with { State = context.State with { PendingQuestion = null, PendingPlanningRequest = null }, Changes = [],
                            RetainedItems = futureSource.Items, Scope = new() };
                        var checkedFuture = await validator.ValidateAsync(future, futureSource, false, Routes(context), timeout.Token, context.Grants);
                        if (checkedFuture.Validation.Errors.Length > 0)
                            return new(Raw(output.Reply), context.State, null, checkedFuture.Validation);
                        return new(Raw(output.Reply),
                            future.State, checkedFuture.Snapshot with { Notes = AgentReplyBuilder.PlanNotes(checkedFuture.Snapshot with { State = future.State }) }, checkedFuture.Validation);
                    }
                    // The user took the budget the reply offered but kept the dishes ("không cần chọn lại"): the same plan is saved with the new budget,
                    // so the draft and the conversation agree and saving is not refused as "lịch chưa theo ngân sách mới" (S1).
                    if (output.Action == "answer" && context.Source is { } keptSource && confirmedState.BudgetOffer is { } offer &&
                        context.State.BudgetAmount == offer && keptSource.State.BudgetAmount != offer)
                    {
                        var kept = keptSource with { State = context.State with { PendingQuestion = null, PendingPlanningRequest = null, BudgetOffer = null }, Changes = [],
                            RetainedItems = keptSource.Items, Scope = new() };
                        var checkedKept = await validator.ValidateAsync(kept, keptSource, false, Routes(context), timeout.Token, context.Grants);
                        telemetry.Record("budget_kept_plan", context.ConversationId, context.TurnId, context.AttemptId, new { offer, checkedKept.Validation.Errors });
                        if (checkedKept.Validation.Errors.Length == 0)
                            return new(Raw(AgentReplyBuilder.FillSlots(output.Reply, checkedKept.Snapshot, keptSource)), kept.State,
                                checkedKept.Snapshot with { Notes = AgentReplyBuilder.PlanNotes(checkedKept.Snapshot with { State = kept.State }) }, checkedKept.Validation);
                    }
                    // Các câu hỏi trước khi lập lịch đi theo thứ tự: chưa rõ địa điểm, số ngày, ngân sách thì chưa hỏi phạm vi hay khẩu vị.
                    // THÍ NGHIỆM: không ép thứ tự hỏi, không kiểm câu; chỉ điền ô {plan} bằng lịch thật.
                    var reply = Raw(output.Action == "answer" && context.Source is not null
                        ? output.Reply.Replace("{plan}", AgentReplyBuilder.Plan(context) ?? "", StringComparison.Ordinal) : output.Reply);
                    // Model đã trả lời thay vì hỏi tiếp nghĩa là câu hỏi cũ không còn mở; câu hỏi khẩu vị còn gắn với yêu cầu đang lập nên giữ.
                    context.State = context.State with { PendingQuestion = output.Action == "clarify" ? reply[..Math.Min(reply.Length, 1000)] : null };
                    // Ghi lại đang hỏi tới đâu và yêu cầu đang lập, để lượt sau backend biết thứ tự và model đọc được cả yêu cầu.
                    // Hỏi mức hay phạm vi khẩu vị khi chưa có lịch cũng là một bước lập lịch ("Lập lịch hộ tôi, tôi thích ăn cay"): ghi nhận để câu trả lời
                    // "cay vừa" ở lượt sau được hiểu là khẩu vị cho lịch, không phải cho một món gợi ý.
                    if (output.Action == "clarify" && !AgentIntakePolicy.HasPlan(context) && output.Clarification is "trip_details" or "place_scope" or "preferences" or "taste_intensity" or "taste_scope")
                    {
                        var stage = output.Clarification switch
                        {
                            "place_scope" => AgentIntakePolicy.Scope, "preferences" => AgentIntakePolicy.Preferences, "trip_details" => AgentIntakePolicy.Details,
                            // Câu hỏi khẩu vị giữ giai đoạn đang có; chưa có thì là bước sở thích khi đã đủ tỉnh và số ngày, ngược lại vẫn ở bước thông tin chuyến đi.
                            _ => context.State.PlanningIntake ?? (AgentReplyBuilder.TripDetailsQuestion(context.State) is null ? AgentIntakePolicy.Preferences : AgentIntakePolicy.Details)
                        };
                        context.State = context.State with { PlanningIntake = stage, PendingPlanningRequest = context.State.PendingPlanningRequest ?? AgentIntakePolicy.CaptureRequest(null, context.Request.Content) };
                        telemetry.Record("intake", context.ConversationId, context.TurnId, context.AttemptId, new { stage });
                    }
                    // Nơi backend vừa định vị được nói kèm câu hỏi: người dùng thấy ngay nếu bị hiểu sai trước khi có món nào được chọn.
                    // THÍ NGHIỆM: không chèn ghi chú địa điểm của backend trước câu hỏi.
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
                catch (ConflictException error) when (error.ErrorCode == "PROPOSAL_NOT_COMMITTABLE")
                {
                    // Không để backend tự ghép lịch thay model. Câu trả lời của bản nháp là template ({total}, {changes}...) chỉ được điền khi có lịch,
                    // nên trả nguyên câu đó làm người dùng thấy chỗ trống (hội thoại 236, 238). Model được sửa một lần với đúng lý do bị từ chối;
                    // vẫn hỏng thì báo lỗi có kiểm soát thay vì hiện template.
                    telemetry.Record("proposal_unusable", context.ConversationId, context.TurnId, context.AttemptId, new { output.Action, output.Scope, reason = error.Message });
                    if (!proposalRepaired && calls + 1 < limits.MaxLlmCalls)
                    {
                        context.RejectedOutput = output;
                        context.RepairFeedback = error.Message;
                        proposalRepaired = repaired = true;
                        continue;
                    }
                    throw Unavailable();
                }
                if (context.RestoredMeals.Length > 0)
                    telemetry.Record("out_of_edit_meals_restored", context.ConversationId, context.TurnId, context.AttemptId, new { output.EditMeals, restored = context.RestoredMeals });
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
                // THÍ NGHIỆM: không chặn, không vòng sửa, không tự thay món (scope/budget/repeat). Món catalog đọc được thì lấy tên, giá; món không đọc được giữ nguyên như model viết.
                if (validated.Validation.Errors.Length > 0)
                {
                    telemetry.Record("validation_ignored", context.ConversationId, context.TurnId, context.AttemptId, new { validated.Validation.Errors });
                    var read = validated.Snapshot.Items.GroupBy(item => item.ItemKey).ToDictionary(group => group.Key, group => group.First());
                    var merged = snapshot with { Items = snapshot.Items.Select(item => read.TryGetValue(item.ItemKey, out var known) && known.DishId == item.DishId ? known : item).ToArray() };
                    // Validator có thể dừng trước khi đọc catalog (thiếu tỉnh, câu hỏi đang chờ...): vẫn điền tên, giá, toạ độ để lịch không hiện trống.
                    var (filled, total) = await validator.ReadCatalogAsync(merged, timeout.Token);
                    // Draft is not blocked, but a total above the user's budget stays visible on the card instead of being presented as within budget.
                    var shown = validated.Validation.Errors.Contains(AgentProposalValidator.BudgetExceeded)
                        ? validated.Validation.Warnings.Append(AgentProposalValidator.BudgetExceeded).Distinct().ToArray() : validated.Validation.Warnings;
                    validated = (filled, validated.Validation with { Errors = [], Warnings = shown, TotalCost = validated.Validation.TotalCost > 0 ? validated.Validation.TotalCost : total });
                }
                // STEP-48: Update context: PreviewOutput.
                // Mới là kết quả chuẩn bị. SendAgentMessageUseCase còn kiểm tra lease rồi mới lưu proposal;
                // chỉ writer chung của confirm/chat-save mới áp dụng proposal sau xác nhận rõ ràng.
                context.PreviewOutput = output;
                // STEP-49: Prepare reviewedState from AgentConstraintGuard.Uncovered.
                // The total of an over-budget draft is the budget the reply offers; a "có" may set exactly this amount without quoting it.
                var draftTotal = validated.Snapshot.Items.Sum(item => item.Price);
                var reviewedState = context.State with { FocusDay = null, FocusMeal = null, PlanningIntake = null,
                    BudgetOffer = context.State.BudgetAmount is { } limit && draftTotal > limit ? draftTotal : null,
                    ReplacedDishNames = AgentScopedPlanning.Replaced(context.State, context.Source, validated.Snapshot),
                    SkippedMeals = AgentConstraintGuard.Uncovered(context.State.SkippedMeals ?? [], validated.Snapshot.Items) };
                // STEP-50: Return the new result.
                // THÍ NGHIỆM: chỉ điền các ô model viết ({total}, {numDays}, {place}, {changes}); không kiểm câu, không viết nối dữ kiện.
                var reviewed = validated.Snapshot with { State = reviewedState };
                return new(Raw(AgentReplyBuilder.WithBudgetNote(AgentReplyBuilder.FillSlots(output.Reply, validated.Snapshot, context.Source),
                        validated.Snapshot, reviewedState.BudgetAmount, context.Source)), reviewedState,
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
            StateRejected(context, error, "intake_last_reply", lastReply);
            return new(Raw(lastReply), turnState, null, null);
        }
        // STEP-56: On any other FluentValidation.ValidationException, return the result of Misunderstood.
        catch (FluentValidation.ValidationException error)
        {
            StateRejected(context, error, "misunderstood", lastReply);
            return Misunderstood(context, turnState, lastReply);
        }
    }

    /// <summary>Records which state rule stopped the turn and how the turn ended; behaviour is unchanged.</summary>
    /// <remarks>Rule names and validator messages are fixed backend text, so they go to the normal log; the rejected values and the reply go to the Development trace only.</remarks>
    private void StateRejected(AgentContext context, FluentValidation.ValidationException error, string handledAs, string? lastReply)
    {
        telemetry.Record("state_rejected", context.ConversationId, context.TurnId, context.AttemptId, new
        {
            handledAs, replyEmpty = string.IsNullOrWhiteSpace(lastReply),
            errors = error.Errors.Select(item => new { property = item.PropertyName, message = item.ErrorMessage })
        });
        telemetry.Trace("state_rejected_detail", context.ConversationId, context.TurnId, context.AttemptId, new
        {
            lastReply, rejected = error.Errors.Select(item => new { property = item.PropertyName, value = item.AttemptedValue })
        });
    }

    /// <summary>Route legs are fetched per day from a paid map service for each validation; a long trip skips them in chat and the saved roadmap fetches a day's route when it is opened.</summary>
    private static bool Routes(AgentContext context) => !AgentScopedPlanning.IsLongTrip(context.State);

    /// <summary>Characters of the turn data the model reads, the measure MaxContextCharacters limits.</summary>
    private static int ContextSize(AgentContext context) => AgentJson.Write(new { context.State, Source = context.PromptSource ?? context.Source,
        History = context.PromptHistory, context.Request, context.Exchanges, context.RejectedOutput }).Length;

    // Số tin nhắn gần nhất luôn giữ cho model khi phải bỏ bớt lịch sử: đủ cho câu hỏi vừa hỏi và câu trả lời của người dùng.
    private const int KeptHistory = 4;

    /// <summary>
    /// Makes the turn fit the context limit with the least loss: first drops the description and tags of each candidate dish in tool results
    /// (ids, names, prices and allowed meals stay, so the model still picks correctly), then leaves the oldest chat messages out of the prompt.
    /// Nothing the user sees changes: the plan is read from the catalog and the chat history from the database.
    /// </summary>
    private void Shrink(AgentContext context)
    {
        for (var index = 0; index < context.Exchanges.Count; index++)
        {
            var exchange = context.Exchanges[index];
            context.Exchanges[index] = exchange with { Results = exchange.Results.Select(CompactCandidates).ToArray() };
        }
        var dropped = 0;
        while (ContextSize(context) > limits.MaxContextCharacters && context.History.Count - context.PromptHistorySkip > KeptHistory)
        {
            context.PromptHistorySkip++;
            dropped++;
        }
        telemetry.Record("context_shrunk", context.ConversationId, context.TurnId, context.AttemptId, new { droppedHistory = dropped, size = ContextSize(context) });
    }

    /// <summary>Removes description and tags from every object inside an "items" array of a tool result.</summary>
    private static System.Text.Json.JsonElement CompactCandidates(System.Text.Json.JsonElement result)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(result.GetRawText());
        void Walk(System.Text.Json.Nodes.JsonNode? current, bool inItems)
        {
            switch (current)
            {
                case System.Text.Json.Nodes.JsonObject obj:
                    if (inItems) { obj.Remove("description"); obj.Remove("tags"); }
                    foreach (var (key, value) in obj.ToArray()) Walk(value, key == "items");
                    break;
                case System.Text.Json.Nodes.JsonArray array:
                    foreach (var element in array) Walk(element, inItems);
                    break;
            }
        }
        Walk(node, false);
        return System.Text.Json.JsonSerializer.SerializeToElement(node);
    }

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
            if (await catalog.NearestProvinceAsync(state.OriginLatitude!.Value, state.OriginLongitude!.Value, AgentIntakePolicy.MaxNearbyKm, ct) is { } nearby)
                context.State = state with { Province = nearby, RadiusKm = AgentIntakePolicy.DefaultRadiusKm };
        }
        // STEP-03b: A roadmap with no dishes keeps its province only in the title the create form wrote ("Khám phá ẩm thực Hà Nội"):
        // take it when the title names exactly one catalog province, so the chat does not ask what the roadmap already says.
        if (string.IsNullOrWhiteSpace(context.State.Province) && context.Roadmap is not null &&
            AgentIntakePolicy.ProvinceInTitle(context.State.Title, provinces) is { } titled)
            context.State = context.State with { Province = titled };
        // STEP-04: Return the fixed answer early when a pinned point has no restaurant around it.
        // THÍ NGHIỆM: không trả câu cố định khi quanh điểm đã chọn không có quán.
        // STEP-05: Update context: IntakeComplete, then return null.
        context.IntakeComplete = context.State.PlanningIntake == AgentIntakePolicy.Preferences;
        return null;
    }

    /// <summary>Same wording as the new-plan template in the system prompt; slots are filled after validation.</summary>
    internal const string LongTripReply = "Lịch {numDays} ngày ở {place} xong rồi nè, tổng khoảng {total}. Bạn xem thử, ưng thì bảo mình chốt nhé!";

    // THÍ NGHIỆM: người dùng chỉ thấy câu model viết, kể cả khi nó rỗng; backend không có câu nào của riêng nó.
    private static string Raw(string? reply) => (reply ?? "").Trim();

    /// <summary>THÍ NGHIỆM: nhận state của model như nó viết. Chỉ giữ chuẩn hóa tên tỉnh, các trường backend sở hữu và FluentValidation; không hỏi xác nhận khi nới điều kiện.</summary>
    private AgentState AcceptRaw(AgentContext context, AgentState? proposed)
    {
        // A province with no restaurants on ANGI never enters the trip: the model words why, the state stays without it.
        var spelled = AgentIntakePolicy.WithCatalogProvince(AgentContextBuilder.KeepKnown(proposed, context.State), context);
        context.UnknownPlace = null;
        var placed = AgentConstraintGuard.RetainPlacements(spelled, context.PlacedRequests);
        try { return statePolicy.Accept(context.State, placed, context.ConstraintBaseline); }
        catch (AgentStateChangeRequiredException change)
        {
            telemetry.Record("state_change_unstaged", context.ConversationId, context.TurnId, context.AttemptId);
            return change.Proposed with { PendingStateChange = context.State.PendingStateChange, SourceRoadmapUpdatedAt = context.State.SourceRoadmapUpdatedAt,
                HistoryStartSequence = context.State.HistoryStartSequence, TranscriptStartSequence = context.State.TranscriptStartSequence };
        }
    }

    /// <summary>Ends a suggest_dish turn with the model's checked wording, or the fixed reply when it wrote none; the plan and state stay as the turn began.</summary>
    private async Task<AgentPreparedTurn> SuggestionTurnAsync(AgentContext context, AgentDishSuggestion suggestion, AgentOutput? output, AgentState turnState, CancellationToken ct)
    {
        // STEP-02: Prepare reply through AgentDishSuggestionPolicy.Worded, call telemetry.Record and RecordReplaced when output exists.
        // THÍ NGHIỆM: chỉ điền ô {name1}…; không kiểm câu, không thêm địa chỉ, không thay bằng câu cố định.
        var reply = Raw(AgentDishSuggestionPolicy.FillSlots(output?.Reply, suggestion));
        telemetry.Record("dish_suggestion", context.ConversationId, context.TurnId, context.AttemptId, new { provider = true, suggestion.Personalized });
        // Câu hiển thị đã được điền dữ kiện nên không còn giống chữ model viết; chỉ tính là bị thay khi phải dùng câu cố định.
        // STEP-03: Return the new result. The user moved on, so an earlier question no longer waits; the plan being built stays in the state and the question in the history.
        // The shown dishes are remembered past this turn, so a later "thêm món đó vào bữa tối ngày 2" or "3 món này vào bữa sáng" knows which dishes are meant.
        var offered = suggestion.Dishes.Where(dish => dish.DishId > 0).Select(dish => new AgentOfferedDish(dish.DishId, dish.Name, dish.RestaurantId, dish.Restaurant)).ToArray();
        return new(reply, turnState with { DishOffered = true, PendingQuestion = null, OfferedDishes = offered.Length > 0 ? offered : turnState.OfferedDishes }, null, null, "chat");
    }

    /// <summary>A fixed question for output the backend cannot use; the turn state is unchanged and the user is never shown a service error for it.</summary>
    private AgentPreparedTurn Misunderstood(AgentContext context, AgentState state, string? reply)
    {
        telemetry.Record("misunderstood", context.ConversationId, context.TurnId, context.AttemptId);
        return new(Raw(reply), state, null, null);
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
