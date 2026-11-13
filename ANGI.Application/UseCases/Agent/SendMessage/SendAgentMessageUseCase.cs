using System.Security.Cryptography;
using System.Text;
using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Agent;
using ANGI.Application.Common.Interfaces.Repositories.Roadmap;
using ANGI.Application.Common.Interfaces.Services.Agent;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Agent;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.DTOs.Agent;
using ANGI.Application.Services.Agent;
using ANGI.Application.Services.Agent.Conversation;
using ANGI.Application.Services.Agent.Proposals;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentValidation;

namespace ANGI.Application.UseCases.Agent.SendMessage;

/// <summary>Implements AGENT-02 with a short claim transaction, external execution and fenced finalization.</summary>
public sealed class SendAgentMessageUseCase(IValidator<SendAgentMessageRequestDto> validator, IAgentRepository repository,
    IRoadmapRepository roadmaps, ICurrentUserService user, IUnitOfWork unitOfWork, TimeProvider clock,
    AgentLimits limits, AgentContextBuilder contextBuilder, AgentOrchestrator orchestrator, IAgentTelemetry telemetry,
    AgentProposalCommitter committer) : ISendAgentMessageUseCase
{
    /// <summary>Replays completed requests before version checks and prevents late or concurrent attempts from writing state.</summary>
    public async Task<AgentTurnDto> ExecuteAsync(long id, SendAgentMessageRequestDto request, CancellationToken ct)
    {
        // STEP-01: Validate the request.
        // id là conversationId. Roadmap cần sửa được xác định qua conversation.RoadmapId,
        // do frontend truyền khi mở conversation; không suy ra quyền truy cập từ ID viết trong prompt.
        await validator.ValidateAndThrowAsync(request, ct);
        // STEP-02: Run AgentConversationRules.ValidateId.
        AgentConversationRules.ValidateId(id);
        // STEP-03: Prepare userId through AgentConversationRules.UserId, hash through Hash, conversation, message and 1 more.
        var userId = AgentConversationRules.UserId(user);
        var hash = Hash(request);
        AgentConversation conversation;
        AgentMessage message;
        long attemptId;
        // STEP-04: Hold the scoped resource for the block below.
        // Giai đoạn 1: transaction ngắn để kiểm tra và nhận quyền xử lý (claim).
        // Lease là quyền tạm thời gồm turnId, attemptId và thời điểm hết hạn; chưa gọi LLM ở đây.
        await using (var transaction = await unitOfWork.BeginTransactionAsync(ct))
        {
            // STEP-05: Set conversation.
            // Khóa conversation đúng chủ để hai request không đồng thời nhận quyền sửa cùng state.
            conversation = await repository.LockOwnedAsync(id, userId, ct) ?? throw AgentConversationRules.Missing();
            // STEP-06: Prepare now from clock.GetUtcNow and existing from repository.GetMessageAsync.
            var now = clock.GetUtcNow().UtcDateTime;
            var existing = request.ClientMessageId is { } suppliedId ? await repository.GetMessageAsync(id, suppliedId, ct) : null;
            // STEP-07: Reject with MESSAGE_ID_REUSED when existing exists and existing.RequestHash is not hash.
            // Một clientMessageId chỉ đại diện cho một nội dung nghiệp vụ trong conversation.
            // Nếu đã completed, trả nguyên response đã lưu trước kiểm tra expiry/version/quota:
            // retry do mất mạng không gọi lại LLM/Recommendation hoặc tạo proposal thứ hai.
            if (existing is not null && existing.RequestHash != hash) throw new ConflictException("MESSAGE_ID_REUSED",
                "clientMessageId đã được dùng cho nội dung khác. Tin nhắn mới cần ID mới, hoặc bỏ clientMessageId và expectedConversationVersion để backend tự cấp.");
            // STEP-08: Return a copy of AgentJson.Read() with updated fields early when existing.ProcessingStatus is Completed and existing.ResponsePayload exists.
            if (existing?.ProcessingStatus == AgentProcessingStatus.Completed && existing.ResponsePayload is not null)
                return AgentJson.Read<AgentTurnDto>(existing.ResponsePayload) with { Replayed = true, ClientMessageId = existing.ClientMessageId };
            // STEP-09: Check the active rule through AgentConversationRules.RequireActive.
            AgentConversationRules.RequireActive(conversation, now);
            // STEP-10: Reject with MESSAGE_EXPIRED when existing is missing, request.ClientMessageId exists and expiredId <= PurgedClientMessageId.
            if (existing is null && request.ClientMessageId is { } expiredId && expiredId <= conversation.PurgedClientMessageId)
                throw new GoneException("MESSAGE_EXPIRED", "Tin nhắn đã hết thời hạn phát lại. Hãy tải lại hội thoại và gửi tin nhắn mới.");
            // STEP-11: Reject with CONVERSATION_BUSY when AgentConversationRules.Busy(conversation, now) holds.
            // Retry chính tin nhắn đang chạy nhận trạng thái processing (controller trả 202).
            // Tin nhắn khác phải chờ, tránh hai lượt chat ghi đè state/proposal của nhau.
            if (AgentConversationRules.Busy(conversation, now))
            {
                if (existing is not null && existing.TurnId == conversation.ProcessingTurnId) return new("processing", "Tin nhắn đang được xử lý.", conversation.Version, ClientMessageId: existing.ClientMessageId);
                throw new ConflictException("CONVERSATION_BUSY", "Hội thoại đang xử lý một tin nhắn khác.");
            }
            // STEP-12: Reject with CONVERSATION_VERSION_CONFLICT when request.ExpectedConversationVersion exists and expectedVersion is not conversation.Version.
            // Prompt-only dùng revision hiện tại dưới khóa; client gửi version vẫn được kiểm tra như trước.
            if (request.ExpectedConversationVersion is { } expectedVersion && expectedVersion != conversation.Version)
                throw new ConflictException("CONVERSATION_VERSION_CONFLICT", "Hãy tải lại hội thoại trước khi gửi tin nhắn.");
            // STEP-13: Load repository.LockUserAsync.
            // Khóa theo user vì quota dùng chung cho mọi conversation của user, kể cả retry failed.
            await repository.LockUserAsync(userId, ct);
            // STEP-14: Throw AgentRateLimitedException when repository.CountAttemptsAsync() >= limits.MessagesPerMinute.
            if (await repository.CountAttemptsAsync(userId, now.AddMinutes(-1), ct) >= limits.MessagesPerMinute) throw new AgentRateLimitedException(60);
            // STEP-15: Throw AgentRateLimitedException when repository.CountAttemptsAsync() >= limits.MessagesPerDay.
            if (await repository.CountAttemptsAsync(userId, now.AddDays(-1), ct) >= limits.MessagesPerDay) throw new AgentRateLimitedException(86400);
            // STEP-16: When conversation.ProcessingTurnId exists, prepare abandoned through repository.GetTurnAsync and check whether abandoned exists.
            if (conversation.ProcessingTurnId is { } oldTurn)
            {
                // Đã qua kiểm tra Busy: lease này đã hết hạn, lượt cũ được đánh dấu failed trước takeover.
                var abandoned = await repository.GetTurnAsync(id, oldTurn, ct);
                if (abandoned is not null) abandoned.ProcessingStatus = AgentProcessingStatus.Failed;
            }
            // STEP-17: When existing is missing, run the branch below.
            // Retry failed tái sử dụng logical message cũ; message mới mới được cấp sequence/turn mới.
            if (existing is null)
            {
                // STEP-18: Call repository.NextSequenceAsync and keep sequence.
                var sequence = await repository.NextSequenceAsync(id, ct);
                // STEP-19: Prepare messageId from Math.Max.
                var messageId = request.ClientMessageId ?? Math.Max(sequence, checked(conversation.PurgedClientMessageId + 1));
                // STEP-20: When request.ClientMessageId is missing, repeat the inner loop.
                // Cấp ID dương chưa dùng dưới khóa conversation, kể cả khi client cũ đã chọn ID bằng sequence này.
                if (request.ClientMessageId is null)
                    while (await repository.GetMessageAsync(id, messageId, ct) is not null) messageId = checked(messageId + 1);
                // STEP-21: Set message.
                message = new AgentMessage { ConversationId = id, SequenceNo = sequence, TurnId = sequence,
                    ClientMessageId = messageId, Role = AgentMessageRole.User, RequestHash = hash };
            }
            else message = existing;
            // STEP-22: Set attemptId.
            // Mỗi lần thử có attemptId tăng dần, dù vẫn là cùng clientMessageId/turnId.
            // FinishAsync dùng ID này để chặn worker cũ trả về muộn sau khi lượt khác đã tiếp quản.
            attemptId = checked(conversation.Version + 1);
            // STEP-23: Prepare previousAttempts from AgentJson.Read.
            var previousAttempts = existing is null ? [] : AgentJson.Read<AgentStoredMessage>(existing.Payload).Attempts ?? [];
            // STEP-24: Update message: Payload and ProcessingStatus.
            message.Payload = AgentJson.Write(new AgentStoredMessage(request.Content.Trim(), previousAttempts.Append(now).ToArray(),
                request.SelectedItemKey, request.Command, request.ConfirmedState));
            message.ProcessingStatus = AgentProcessingStatus.Processing;
            // STEP-25: When existing is missing, call repository.AddMessage.
            if (existing is null) repository.AddMessage(message);
            // STEP-26: Update conversation: ProcessingTurnId, ProcessingAttemptId, ProcessingUntil, Version and 1 more.
            conversation.ProcessingTurnId = message.TurnId;
            conversation.ProcessingAttemptId = attemptId;
            conversation.ProcessingUntil = now.AddSeconds(limits.LeaseSeconds);
            conversation.Version = attemptId;
            conversation.ExpiresAt = now.AddDays(limits.ActiveDays);
            // STEP-27: Save the pending changes.
            await unitOfWork.SaveChangesAsync(ct);
            // STEP-28: Commit the transaction.
            await transaction.CommitAsync(ct);
        }
        // STEP-29: Prepare claimedVersion, then call telemetry.Record.
        // Giai đoạn 2: đã thả khóa DB; các lời gọi LLM, Recommendation, Map chạy bên ngoài transaction.
        var claimedVersion = conversation.Version;
        telemetry.Record("claimed", id, message.TurnId, attemptId);
        // Chỉ vào trace Development: nguyên văn người dùng gửi.
        telemetry.Trace("user_message", id, message.TurnId, attemptId,
            new { request.Content, request.Command, request.SelectedItemKey, request.ConfirmedState });
        try
        {
            // STEP-30: Prepare prepared, context and control from the button command.
            // Chỉ nút bấm đi thẳng; câu "bỏ nháp"/"làm lại" người dùng gõ luôn do model đọc, kể cả khi roadmap đã gắn bị xóa.
            AgentPreparedTurn prepared;
            AgentContext? context = null;
            var control = request.Command is "discard" or "reset" ? request.Command : null;
            // STEP-31: When control is "discard" or "reset", prepare state through AgentJson.Read, check whether control is "reset" and more.
            if (control is "discard" or "reset")
            {
                // Lệnh rõ ràng từ request được xử lý trực tiếp, không cần LLM diễn giải.
                // discard giữ yêu cầu chuyến đi; reset bắt đầu kế hoạch mới và tách roadmap đã gắn.
                // Cả hai chỉ bỏ proposal, không xóa hoặc sửa roadmap chính thức.
                var state = AgentJson.Read<AgentState>(conversation.State);
                if (control == "reset") state = await ResetStateAsync(conversation, message.SequenceNo, ct);
                prepared = new(control == "reset" ? "Đã bắt đầu kế hoạch mới. Bạn muốn ăn ở đâu, trong mấy ngày và với ngân sách bao nhiêu? Lịch đã lưu vẫn được giữ nguyên."
                    : "Đã bỏ bản đề xuất đang chỉnh sửa. Lịch đã lưu vẫn được giữ nguyên.",
                    state with { PendingQuestion = null, PendingStateChange = null, PendingPlanningRequest = null, PlanningIntake = null, FocusDay = null, FocusMeal = null }, null, null, control);
            }
            else
            {
                // Nạp state, proposal hiện hành, roadmap mới nhất (nếu có) và history completed.
                // Ví dụ prompt chỉ nói "sửa roadmap cho tôi": model được hướng dẫn hỏi rõ phần cần sửa.
                // ContextBuilder chỉ nạp roadmap đã gắn; luồng này chưa có tool tìm/chọn roadmap khác.
                context = await contextBuilder.BuildAsync(conversation, request, ct);
                context.TurnId = message.TurnId;
                context.AttemptId = attemptId;
                // Câu hỏi lưu chỉ sống đúng một lượt: mọi tin nhắn không trả lời nó đều kết thúc việc chờ, để "ok" về sau không lưu nhầm.
                var pendingSave = context.State.PendingSave;
                // Lời mời thêm món cũng chỉ sống một lượt.
                context.DishOffered = context.State.DishOffered == true;
                context.State = context.State with { PendingSave = null, DishOffered = null };
                var today = AgentConfirmationPolicy.Today(clock);
                // Orchestrator gọi LLM/tools rồi build + validate; kết quả ở đây vẫn chưa được lưu.
                // Câu lưu đầy đủ và ngày viết bằng số không phụ thuộc cách nói, nên không cần gọi LLM: chỉ backend được quyết định ghi roadmap.
                // THÍ NGHIỆM: không đọc câu lưu hay ngày viết bằng số bằng regex; mọi tin nhắn đi qua model.
                {
                    // Model đọc mọi cách nói; backend chỉ cho câu trả lời đó có hiệu lực khi đang có đúng câu hỏi này.
                    context.AwaitedAnswer = AgentConfirmationPolicy.Awaited(context.State, context.CurrentProposal, pendingSave);
                    var asked = context.State;
                    prepared = await orchestrator.RunAsync(context, ct);
                    if (prepared.Answer is { } answer)
                        prepared = await AnswerTurnAsync(conversation, userId, context, asked, pendingSave, answer, today, ct, prepared.Reply);
                    else if (prepared.Action == "request_save")
                    {
                        // Muốn lưu nghĩa là không còn cần câu hỏi làm rõ đang mở, nên về đúng điều kiện của bản đề xuất. Việc này chưa ghi gì: bước sau vẫn hỏi trước khi lưu.
                        context.State = context.AwaitedAnswer == AgentConfirmationPolicy.AwaitingQuestion ? context.CurrentProposal!.Snapshot.State with { PendingQuestion = null } : asked;
                        // Chỉ lưu khi lịch còn khớp điều kiện chuyến đi. Lệch thì không ghi gì và giữ câu model viết:
                        // model đã được báo planMatchesTrip=false nên tự nói lịch chưa theo điều kiện mới, thay vì người dùng nhận lỗi 409.
                        // Có ngày bắt đầu thì lưu ngay; chưa có thì chờ ngày (model được báo saveNeedsStartDate=true nên tự hỏi), "để sau" vẫn lưu được không ngày.
                        if (AgentConfirmationPolicy.PlanMatchesTrip(context.State, context.CurrentProposal))
                            prepared = await SaveTurnAsync(conversation, userId, context, AgentConfirmationPolicy.SaveWish(context, today), ct, prepared.Reply);
                        else telemetry.Record("save_skipped", id, message.TurnId, attemptId, new { reason = context.CurrentProposal?.Status == "ready" ? "plan_mismatch" : "no_ready_plan" });
                    }
                    // Lời chào hay câu ngoài phạm vi không trả lời câu hỏi lưu nhưng cũng không hủy nó.
                    // Vừa gợi ý món thì khác: "ok" tiếp theo là đáp lời mời đó, không được hiểu thành đồng ý lưu lịch.
                    else if (pendingSave is not null && prepared.State.DishOffered != true && prepared.Action is "chat" or "food_info" or "out_of_scope")
                        prepared = prepared with { State = prepared.State with { PendingSave = pendingSave } };
                    // Lời mời gợi ý món cũng còn qua một câu chat, câu hỏi về món hay câu ngoài phạm vi chen giữa (hội thoại 220: "sở thích của tôi là gì"
                    // rồi "tôi thích ăn cay"), để khẩu vị nói sau đó vẫn là về món vừa gợi ý. Lập lịch hay sửa lịch thì lời mời kết thúc.
                    if (context.DishOffered && prepared.Answer is null && prepared.Action is "chat" or "food_info" or "out_of_scope" && prepared.State.DishOffered != true)
                        prepared = prepared with { State = prepared.State with { DishOffered = true } };
                }
                if (prepared.Action == "reset") prepared = prepared with { State = await ResetStateAsync(conversation, message.SequenceNo, ct) };
                else if (prepared.Action == "discard") prepared = prepared with
                    { State = context.State with { PendingQuestion = null, PendingStateChange = null, PendingPlanningRequest = null, PlanningIntake = null, FocusDay = null, FocusMeal = null } };
            }
            // STEP-32: When context exists and prepared.Snapshot exists, set prepared.
            // Mọi đường tạo bản nháp, kể cả không qua model, đều đi qua đây nên phần giới thiệu luôn khớp với món cuối cùng.
            if (context is not null && prepared.Snapshot is { } drafted) prepared = prepared with { Snapshot = AgentReplyBuilder.AttachNotes(drafted, context) };
            // A restaurant name that found several branches this turn is one the user is now asked to choose for; only then may a later
            // turn resolve it to a branch with a longer name (Round 3, AG-22: a branch was picked on the first turn without asking).
            if (context is { AmbiguousNames.Count: > 0 })
                prepared = prepared with { State = prepared.State with { CatalogChoices = [.. (prepared.State.CatalogChoices ?? []).Concat(context.AmbiguousNames).Distinct().TakeLast(10)] } };
            // STEP-33: Return the result of FinishAsync.
            // Giai đoạn 3: nhận lại khóa và kiểm tra quyền ghi trước khi lưu trọn kết quả một lượt.
            return await FinishAsync(conversation.Id, userId, message, attemptId, claimedVersion, request, context, prepared, ct);
        }
        catch (Exception error)
        {
            // STEP-34: On Exception, call telemetry.Record, call FailAsync and 1 more action(s).
            telemetry.Record("turn_failed", id, message.TurnId, attemptId, new
                { errorCode = (error as AppException)?.ErrorCode, errorType = error.GetType().Name });
            telemetry.Trace("error", id, message.TurnId, attemptId, new { error.Message, detail = error.ToString() });
            await FailAsync(id, userId, message, attemptId);
            throw;
        }
    }

    /// <summary>Turns a save step into a turn: a commit prepared for the question's exact proposal, or a reply that may keep waiting.</summary>
    private async Task<AgentPreparedTurn> SaveTurnAsync(AgentConversation conversation, int userId, AgentContext context, AgentSaveDecision save, CancellationToken ct, string? modelReply = null)
    {
        // STEP-01: Return the new result early when the step does not commit.
        if (!save.Commit) return new(WithBackend(modelReply, save.Reply!), context.State with { PendingSave = save.Pending }, null, null);
        // STEP-02: Prepare proposal and commit through committer.PrepareAsync, checked against the state this turn ends in.
        var proposal = context.CurrentProposal!;
        var commit = await committer.PrepareAsync(conversation, userId, new ConfirmAgentProposalRequestDto
            { ProposalId = proposal.ProposalId, ProposalVersion = proposal.Version, StartDate = save.StartDate }, true, ct, context.State);
        // STEP-03: Return the new result.
        return new(WithBackend(modelReply, AgentReplyBuilder.Saved(proposal.Snapshot.State.NumDays, save.StartDate ?? context.State.StartDate ?? proposal.Snapshot.State.StartDate)),
            context.State, null, null, "confirm", commit);
    }

    // THÍ NGHIỆM: chỉ hiện câu model viết; câu cố định của backend bị bỏ.
    private static string WithBackend(string? modelReply, string backend) => (modelReply ?? "").Trim();

    private static string Spoken(string? modelReply, string backend) => (modelReply ?? "").Trim();

    /// <summary>Carries out what the model read in a reply to the open question; the same steps run whatever words the user chose.</summary>
    private async Task<AgentPreparedTurn> AnswerTurnAsync(AgentConversation conversation, int userId, AgentContext context, AgentState asked, AgentPendingSave? pendingSave,
        AgentAnswer answer, DateOnly today, CancellationToken ct, string? modelReply = null)
    {
        // STEP-01: Keep the state the question was asked in, and read nothing else from this model call.
        context.State = asked;
        context.AwaitedAnswer = null;
        // STEP-02: Return the result of SaveTurnAsync early when the answer is about saving.
        if (asked.PendingStateChange is null && AgentConfirmationPolicy.IsCurrent(pendingSave, context.CurrentProposal))
            return await SaveTurnAsync(conversation, userId, context, AgentConfirmationPolicy.AnswerSave(context, pendingSave!, answer, today), ct, modelReply);
        // STEP-03: When a staged change waits, drop it on a refusal or when the roadmap moved since the question.
        if (asked.PendingStateChange is { } change)
        {
            if (answer.Kind == AgentConfirmationPolicy.Decline)
                return new(Spoken(modelReply, "Đã giữ nguyên các điều kiện trước đó."), asked with { PendingQuestion = null, PendingStateChange = null }, null, null);
            if (change.SourceRoadmapUpdatedAt != asked.SourceRoadmapUpdatedAt)
                return new(Spoken(modelReply, ""),
                    asked with { PendingQuestion = null, PendingStateChange = null }, null, null);
            // STEP-04: Apply the staged change and plan again, the only answer that needs another model call.
            context.State = change with { PendingQuestion = null, PendingStateChange = null, FocusDay = asked.FocusDay, FocusMeal = asked.FocusMeal };
            context.ReplanAfterConfirmation = true;
            return await orchestrator.RunAsync(context, ct);
        }
        // STEP-05: Return to the proposal's own conditions: a declined clarification no longer blocks saving.
        return new(Spoken(modelReply, "Mình giữ nguyên lịch hiện tại nhé."), context.CurrentProposal!.Snapshot.State with { PendingQuestion = null }, null, null);
    }

    private async Task<AgentState> ResetStateAsync(AgentConversation conversation, long sequence, CancellationToken ct)
    {
        // STEP-01: Prepare roadmap from roadmaps.GetOwnedDetailAsync and state from AgentContextBuilder.StateFromRoadmap.
        // Roadmap đã bị xóa thì bắt đầu trống thay vì trả 404; FinishAsync gỡ liên kết.
        var roadmap = conversation.RoadmapId is { } id ? await roadmaps.GetOwnedDetailAsync(id, conversation.UserId, ct) : null;
        var state = roadmap is not null ? AgentContextBuilder.StateFromRoadmap(roadmap) : new AgentState();
        // STEP-02: Return a copy of state with updated fields.
        // Reset chỉ cắt lịch sử model đọc; phần chat người dùng thấy vẫn giữ (hội thoại 232 mất hết tin nhắn sau mỗi lần reset).
        return state with { HistoryStartSequence = sequence, TranscriptStartSequence = AgentJson.Read<AgentState>(conversation.State).TranscriptStartSequence };
    }

    /// <summary>Stores a result only while the same unexpired attempt still owns the claimed conversation revision.</summary>
    private async Task<AgentTurnDto> FinishAsync(long id, int userId, AgentMessage message, long attemptId, long claimedVersion,
        SendAgentMessageRequestDto request, AgentContext? context, AgentPreparedTurn prepared, CancellationToken ct)
    {
        // STEP-01: Open the database transaction.
        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);
        // STEP-02: Prepare conversation from AgentConversationRules.Missing and now from clock.GetUtcNow.
        var conversation = await repository.LockOwnedAsync(id, userId, ct) ?? throw AgentConversationRules.Missing();
        var now = clock.GetUtcNow().UtcDateTime;
        // STEP-03: Reject with CONVERSATION_VERSION_CONFLICT when conversation.ProcessingAttemptId is not attemptId and more.
        // Chỉ attempt đã claim mới được ghi, khi lease chưa hết và conversation chưa đổi version.
        // LLM trả kết quả thành công không đủ để cho phép một worker đã mất quyền ghi vào DB.
        if (conversation.ProcessingAttemptId != attemptId || conversation.ProcessingUntil <= now || conversation.Version != claimedVersion)
            throw new ConflictException("CONVERSATION_VERSION_CONFLICT", "Lượt xử lý đã hết quyền ghi. Hãy tải lại hội thoại.");
        // STEP-04: Check the active rule through AgentConversationRules.RequireActive.
        AgentConversationRules.RequireActive(conversation, now);
        // STEP-05: When context.Roadmap exists, prepare currentRoadmap through roadmaps.LockAndGetTrackedAsync and more.
        if (context?.Roadmap is { } sourceRoadmap)
        {
            // Trong lúc gọi dịch vụ ngoài, người dùng hoặc conversation khác có thể đã sửa roadmap.
            // Chặn lưu proposal dựa trên snapshot cũ thay vì âm thầm bỏ qua chỉnh sửa mới.
            var currentRoadmap = await roadmaps.LockAndGetTrackedAsync(sourceRoadmap.Id, userId, ct);
            if (currentRoadmap is null || currentRoadmap.UpdatedAt != sourceRoadmap.UpdatedAt)
                throw new ConflictException("ROADMAP_UPDATED_AT_CONFLICT", "Roadmap đã thay đổi trong lúc xử lý.");
        }
        // STEP-06: Prepare previous from repository.GetProposalAsync, created and commitResult.
        var previous = conversation.CurrentProposalId is { } proposalId ? await repository.GetProposalAsync(id, proposalId, ct) : null;
        // Bản nháp dựng trên bản lịch đã bị sửa tay hay xóa đã bị bỏ khỏi context: bỏ luôn trong DB để UI, GET và lời model nói khớp nhau.
        if (context?.OutdatedProposalDropped == true && previous?.Status == AgentProposalStatus.Ready)
        {
            previous.Status = AgentProposalStatus.Discarded;
            conversation.CurrentProposalId = null;
            conversation.CurrentProposal = null;
            previous = null;
        }
        // The turn that met a deleted roadmap has told the user; unlinking it makes the next turns an ordinary new chat instead of
        // repeating the notice on every reply (Round 3, AG-60).
        if (context?.AttachedRoadmapDeleted == true) conversation.RoadmapId = null;
        AgentProposal? created = null;
        AgentCommitDto? commitResult = null;
        // STEP-07: When prepared.Commit exists, prepare committed through committer.CommitLockedAsync, set commitResult and 1 more action(s).
        if (prepared.Commit is { } commit)
        {
            // Kiểm tra theo state lượt này kết thúc: muốn lưu có thể đã đóng câu hỏi làm rõ mà state đã lưu vẫn còn mở.
            var committed = await committer.CommitLockedAsync(conversation, commit, userId, true, ct, prepared.State);
            commitResult = committed.Result;
            prepared = prepared with { State = AgentJson.Read<AgentState>(conversation.State) };
        }
        // STEP-08: When prepared.Action is one of the listed values, run the branch below.
        if (prepared.Action is "discard" or "reset" or "state_changed")
        {
            // STEP-09: When previous.Status is AgentProposalStatus.Ready, set previous.Status.
            // Bỏ bản nháp hiện hành; bản lịch chính thức không bị thay đổi.
            if (previous?.Status == AgentProposalStatus.Ready) previous.Status = AgentProposalStatus.Discarded;
            // STEP-10: Update conversation: CurrentProposalId and CurrentProposal.
            conversation.CurrentProposalId = null;
            conversation.CurrentProposal = null;
            // STEP-11: When prepared.Action is "reset", conversation.RoadmapId exists and roadmaps.GetOwnedDetailAsync() is missing, set conversation.RoadmapId.
            // Reset clears chat context, never the attached roadmap identity, unless that roadmap no longer exists.
            if (prepared.Action == "reset" && conversation.RoadmapId is { } attached && await roadmaps.GetOwnedDetailAsync(attached, userId, ct) is null)
                conversation.RoadmapId = null;
        }
        else if (prepared.Snapshot is not null)
        {
            // Snapshot đã qua Validator mới được lưu Ready. Mỗi lần sửa tạo proposal version mới,
            // giữ bản trước dưới trạng thái Superseded thay vì sửa đè payload lịch sử.
            if (previous?.Status == AgentProposalStatus.Ready) previous.Status = AgentProposalStatus.Superseded;
            created = new AgentProposal
            {
                Id = await repository.ReserveProposalIdAsync(ct), ConversationId = id,
                ProposalVersion = await repository.NextProposalVersionAsync(id, ct),
                Operation = prepared.Snapshot.BaseRoadmapId.HasValue ? AgentProposalOperation.Update : AgentProposalOperation.Create,
                BaseRoadmapId = prepared.Snapshot.BaseRoadmapId, BaseRoadmapUpdatedAt = prepared.Snapshot.BaseRoadmapUpdatedAt,
                Payload = AgentJson.Write(prepared.Snapshot), ValidationResult = AgentJson.Write(prepared.Validation),
                Status = AgentProposalStatus.Ready, ExpiresAt = now.AddDays(limits.ActiveDays),
                RecoRequestIds = prepared.Snapshot.Items.Where(x => x.RequestId.HasValue).Select(x => x.RequestId!.Value).Distinct().ToArray()
            };
            repository.AddProposal(created);
            conversation.CurrentProposalId = created.Id;
            conversation.CurrentProposal = created;
        }
        // STEP-12: When previous exists, prepared.Snapshot is missing, prepared.Commit is missing and 2 more condition(s), set prepared.
        // clarify/answer hoặc validation có lỗi không tạo snapshot mới: giữ proposal cũ để xem lại.
        // State mới vẫn được lưu; Confirm sẽ kiểm tra nó có còn khớp proposal trước khi cho commit.
        // Một câu hỏi đã cho thấy lịch chưa đổi và thẻ bản nháp vẫn đang hiện; chỉ báo khi lượt này không lập được lịch.
        // THÍ NGHIỆM: không viết nối "Bản đề xuất trước đó được giữ lại…" vào câu của model.
        // STEP-13: Update conversation: State.
        conversation.State = AgentJson.Write(prepared.State);
        // STEP-14: Increment conversation.Version.
        conversation.Version++;
        // STEP-15: Update conversation: ExpiresAt.
        conversation.ExpiresAt = now.AddDays(limits.ActiveDays);
        // STEP-16: Call AgentConversationRules.ClearLease.
        AgentConversationRules.ClearLease(conversation);
        // STEP-17: Prepare previousStillApplies from AgentJson.Read and result as AgentTurnDto.
        var previousStillApplies = previous is not null && prepared.Action is not ("discard" or "reset" or "state_changed") &&
            prepared.State.PendingQuestion is null && prepared.State.PendingStateChange is null && prepared.State.PendingPlanningRequest is null &&
            prepared.Validation?.Errors.Length is not > 0 &&
            AgentConversationRules.SameConstraints(prepared.State, AgentJson.Read<AgentSnapshot>(previous.Payload).State);
        var result = new AgentTurnDto("completed", prepared.Reply, conversation.Version,
            created is not null ? AgentContextBuilder.MapProposal(created) : previousStillApplies ? AgentContextBuilder.MapProposal(previous!) : null,
            prepared.Validation, Commit: commitResult, ClientMessageId: message.ClientMessageId);
        // STEP-18: Update message: ProcessingStatus and ResponsePayload.
        // Lưu response cùng logical user message để request gửi lại có thể replay chính xác kết quả này.
        message.ProcessingStatus = AgentProcessingStatus.Completed;
        message.ResponsePayload = AgentJson.Write(result);
        // STEP-19: Call repository.NextSequenceAsync and keep sequence.
        var sequence = await repository.NextSequenceAsync(id, ct);
        // STEP-20: When context exists, go through each exchange in context.Exchanges.
        if (context is not null)
        {
            // Lưu call/result của tools để truy vết nguồn món; không đưa attempt dở vào history completed.
            foreach (var exchange in context.Exchanges)
                repository.AddMessage(new AgentMessage { ConversationId = id, SequenceNo = sequence++, TurnId = message.TurnId,
                    Role = AgentMessageRole.Tool, Payload = AgentJson.Write(new AgentStoredMessage(AgentJson.Write(exchange))) });
        }
        // STEP-21: Queue a new AgentMessage through repository.AddMessage.
        repository.AddMessage(new AgentMessage { ConversationId = id, SequenceNo = sequence, TurnId = message.TurnId,
            Role = AgentMessageRole.Assistant, Payload = AgentJson.Write(new AgentStoredMessage(prepared.Reply)) });
        // STEP-22: Save the pending changes.
        // Proposal, state, transcript và response replay cùng thành công hoặc cùng rollback.
        // Chat-save dùng cùng writer với Confirm; roadmap và phản hồi chat nằm trong cùng transaction.
        await unitOfWork.SaveChangesAsync(ct);
        // STEP-23: Commit the transaction.
        await transaction.CommitAsync(ct);
        // STEP-24: Call telemetry.Record.
        telemetry.Record("completed", id, message.TurnId, attemptId, new { proposalVersion = created?.ProposalVersion });
        // Chỉ vào trace Development: câu trả lời, bản đề xuất và state cuối mà người dùng nhận được.
        telemetry.Trace("final_reply", id, message.TurnId, attemptId, new { prepared.Action, result.Reply, result.Proposal, result.Validation, state = prepared.State });
        // STEP-25: When commitResult exists, call telemetry.Record.
        if (commitResult is not null) telemetry.Record("committed", id, message.TurnId, attemptId,
            new { commitResult.RoadmapId, commitResult.RoadmapUpdatedAt, commitResult.CommittedProposalId });
        // STEP-26: Return result.
        return result;
    }

    /// <summary>Releases only this failed attempt; a superseding worker's lease and state remain untouched.</summary>
    private async Task FailAsync(long id, int userId, AgentMessage message, long attemptId)
    {
        // Token riêng để vẫn dọn được lease trong tối đa 5 giây khi request gốc đã bị hủy.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            // Bỏ tracking sau rollback, tránh SaveChanges lần dọn lỗi lưu nhầm proposal/state chưa hoàn tất.
            repository.ResetTracking();
            await using var transaction = await unitOfWork.BeginTransactionAsync(timeout.Token);
            var conversation = await repository.LockOwnedAsync(id, userId, timeout.Token);
            // Nếu attempt khác đã tiếp quản, không xóa lease hoặc thay message/state của worker mới.
            if (conversation?.ProcessingAttemptId != attemptId) return;
            var failed = await repository.GetMessageAsync(id, message.ClientMessageId!.Value, timeout.Token);
            if (failed is null) return;
            // Cho phép retry logical message này với attempt mới; không để response dở được replay.
            failed.ProcessingStatus = AgentProcessingStatus.Failed;
            failed.ResponsePayload = null;
            AgentConversationRules.ClearLease(conversation);
            conversation.Version++;
            await unitOfWork.SaveChangesAsync(timeout.Token);
            await transaction.CommitAsync(timeout.Token);
            telemetry.Record("failed", id, message.TurnId, attemptId);
        }
        catch (Exception failure)
        {
            // Nếu DB chưa sẵn sàng để dọn, lease hết hạn vẫn chặn ghi từ worker cũ.
            // Chỉ log lỗi phục hồi, giữ nguyên lỗi ban đầu mà ExecuteAsync sẽ trả cho caller.
            telemetry.Record("failure_cleanup", id, message.TurnId, attemptId, new { errorType = failure.GetType().Name });
        }
    }

    /// <summary>Hashes normalized business content and explicit selections while excluding transport version.</summary>
    // SHA-256 của nội dung đã trim + item được chọn + command + confirmedState đã chuẩn hóa.
    // Không hash expectedConversationVersion: retry sau reload được gửi version mới nhưng vẫn là cùng yêu cầu.
    // Hash dùng phát hiện nội dung khác với cùng clientMessageId, không dùng để xác thực hoặc mã hóa dữ liệu.
    private static string Hash(SendAgentMessageRequestDto request) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        AgentJson.Write(new { content = request.Content.Trim(), request.SelectedItemKey, request.Command,
            confirmedState = request.ConfirmedState is null ? null : AgentConversationRules.CanonicalState(request.ConfirmedState) })))).ToLowerInvariant();
}
