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
        // Timeout áp dụng cho toàn bộ vòng LLM/tools của lượt chat; ct vẫn giữ quyền hủy từ caller.
        // Use case đã commit bước claim trước khi vào đây nên không giữ transaction DB trong lúc chờ AI.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(limits.TimeoutSeconds));
        var toolRounds = 0;
        var toolCalls = 0;
        var repaired = false;
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
                var step = await llm.RunAsync(context, repaired, timeout.Token);
                telemetry.Record("llm", context.ConversationId, context.TurnId, context.AttemptId, new { elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds, step?.Tokens });
                // Một bước phải có tools HOẶC output cuối. Null/cả hai/cả hai rỗng là kết quả không hợp lệ.
                // Cho phép một lần yêu cầu sửa output; sửa vẫn lỗi thì trả AGENT_UNAVAILABLE.
                if (step is null || step.Tools is null || step.Tools.Length == 0 && step.Output is null || step.Tools.Length > 0 && step.Output is not null)
                {
                    if (repaired) throw Unavailable();
                    repaired = true;
                    continue;
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
                // Action do LLM chọn theo Instructions trong adapter; hợp đồng cho phép đúng bốn action
                // được khai báo ở AgentLlmSchema.Output, đồng thời kiểm tra lại allowlist tại đây.
                // Đây là quy tắc xử lý của ANGI, không có điểm số confidence/threshold phân loại intent riêng.
                if (string.IsNullOrWhiteSpace(output.Reply) || output.Reply.Length > limits.MaxMessageLength ||
                    output.Items is null || output.Changes is null || output.Scope is null ||
                    output.Action is not ("clarify" or "answer" or "create_proposal" or "patch_proposal"))
                {
                    if (repaired) throw Unavailable();
                    repaired = true;
                    continue;
                }
                // Model có thể bổ sung/siết điều kiện, nhưng không được tự nới ràng buộc đã chốt.
                // Cờ ConstraintsChangeConfirmed của model không thay thế confirmedState từ người dùng.
                context.State = statePolicy.Accept(context.State, output.State);
                if (output.Action is "clarify" or "answer")
                {
                    // clarify: thiếu tỉnh/ngày/ngân sách, chưa rõ món hoặc nội dung cần sửa thì hỏi lại.
                    // answer: giải thích bằng ngữ cảnh sẵn có. Cả hai không được kèm items/changes để sửa lịch.
                    // Với prompt "sửa roadmap cho tôi" mà chưa rõ sửa gì, Instructions hướng dẫn clarify;
                    // code ở đây kiểm tra hợp đồng output, không tự hiểu/chấm đúng sai ngữ nghĩa câu hỏi.
                    if (output.Items.Length != 0 || output.Changes.Length != 0) throw Unavailable();
                    if (output.Action == "clarify") context.State = context.State with
                        { PendingQuestion = output.Reply[..Math.Min(output.Reply.Length, 1000)] };
                    return new(output.Reply, context.State, null, null);
                }
                // create_proposal: lịch mới; patch_proposal: add/replace/remove/move trên lịch nguồn.
                // Builder kiểm tra nguồn, scope và baseProposalVersion; Validator kiểm tra catalog,
                // nguồn candidate, giá, ngân sách và tuyến. Action hợp lệ chưa có nghĩa proposal hợp lệ.
                var snapshot = builder.Build(context, output);
                var validated = await validator.ValidateAsync(snapshot, context.Source, false, true, timeout.Token, context.Grants);
                telemetry.Record("validation", context.ConversationId, context.TurnId, context.AttemptId, new { validated.Validation.Errors, validated.Validation.Warnings });
                // Có lỗi thì không trả snapshot mới để lưu Ready; proposal trước được giữ lại.
                if (validated.Validation.Errors.Length > 0) return new("Đề xuất chưa hợp lệ. Vui lòng làm rõ hoặc điều chỉnh các điều kiện.", context.State, null, validated.Validation);
                // Mới là kết quả chuẩn bị. SendAgentMessageUseCase còn kiểm tra lease rồi mới lưu proposal;
                // chỉ ConfirmAgentProposalUseCase mới áp dụng vào roadmap sau xác nhận riêng.
                return new(output.Reply, context.State, validated.Snapshot, validated.Validation);
            }
            // Đã dùng hết số lần gọi cho phép mà chưa có kết quả cuối hợp lệ.
            throw Unavailable();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw Unavailable(); }
        catch (FluentValidation.ValidationException) { throw Unavailable(); }
    }

    /// <summary>Creates a controlled provider/limit error without replacing the last ready proposal.</summary>
    private static ServiceUnavailableException Unavailable() => new("AGENT_UNAVAILABLE", "Agent chưa hoàn tất yêu cầu. Vui lòng thử lại.");
}

/// <summary>Prepared turn data that must pass lease fencing before persistence.</summary>
public sealed record AgentPreparedTurn(string Reply, AgentState State, AgentSnapshot? Snapshot, AgentValidation? Validation);
