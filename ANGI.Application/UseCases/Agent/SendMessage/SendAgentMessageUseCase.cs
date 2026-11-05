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
        try
        {
            // STEP-30: Prepare prepared, context and control from AgentChatPolicy.DraftControl.
            AgentPreparedTurn prepared;
            AgentContext? context = null;
            var control = request.Command is "discard" or "reset" ? request.Command : AgentChatPolicy.DraftControl(request.Content);
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
                context.State = context.State with { PendingSave = null };
                // Orchestrator gọi LLM/tools rồi build + validate; kết quả ở đây vẫn chưa được lưu.
                if (context.State.PendingStateChange is { } stalePending && AgentChatPolicy.AcceptsChange(request.Content) &&
                    stalePending.SourceRoadmapUpdatedAt != context.State.SourceRoadmapUpdatedAt)
                    prepared = new("Lịch vừa thay đổi sau câu hỏi xác nhận. Bạn nêu lại điều kiện muốn đổi để mình kiểm tra bản mới nhất nhé.",
                        context.State with { PendingQuestion = null, PendingStateChange = null }, null, null);
                else if (context.State.PendingStateChange is { } pending && AgentChatPolicy.AcceptsChange(request.Content))
                {
                    context.State = pending with { PendingQuestion = null, PendingStateChange = null,
                        FocusDay = context.State.FocusDay, FocusMeal = context.State.FocusMeal };
                    context.ReplanAfterConfirmation = true;
                    prepared = await orchestrator.RunAsync(context, ct);
                }
                else if (context.State.PendingStateChange is not null && AgentChatPolicy.RejectsChange(request.Content))
                    prepared = new("Đã giữ nguyên các điều kiện trước đó.", context.State with { PendingQuestion = null, PendingStateChange = null }, null, null);
                // Từ chối một câu hỏi làm rõ khi đã có lịch: đóng câu hỏi, không gọi LLM, để lịch hiện tại lưu được ngay.
                else if (context.State.PendingQuestion is not null && context.State.PendingStateChange is null && context.State.PendingPlanningRequest is null &&
                    context.CurrentProposal?.Status == "ready" && AgentChatPolicy.DismissesQuestion(request.Content))
                    // Về đúng điều kiện của bản đề xuất: một yêu cầu đổi chưa thành lịch (ví dụ quán không có món cho bữa đó) sẽ không còn chặn việc lưu.
                    prepared = new("Mình giữ nguyên lịch hiện tại nhé. Ưng rồi thì cứ bảo mình chốt.", context.CurrentProposal.Snapshot.State with { PendingQuestion = null }, null, null);
                else if (AgentChatPolicy.IsReviewRequest(request.Content) && context.CurrentProposal?.Status == "ready" &&
                    context.State.PendingQuestion is null && context.State.PendingStateChange is null && context.State.PendingPlanningRequest is null &&
                    AgentConversationRules.SameConstraints(context.State, context.CurrentProposal.Snapshot.State))
                    prepared = new("Lịch hiện tại ở thẻ bên dưới, bạn xem lại nhé. Ưng rồi thì cứ bảo mình chốt.", context.State, null, null);
                // Việc lưu và câu hỏi ngày đi không cần LLM: chỉ backend được quyết định ghi roadmap.
                else if (AgentSavePolicy.Decide(context, pendingSave, AgentDatePolicy.Today(clock)) is { } save)
                {
                    if (save.Commit)
                    {
                        var proposal = context.CurrentProposal!;
                        var commit = await committer.PrepareAsync(conversation, userId, new ConfirmAgentProposalRequestDto
                            { ProposalId = proposal.ProposalId, ProposalVersion = proposal.Version, StartDate = save.StartDate }, true, ct);
                        prepared = new(AgentReplyBuilder.Saved(proposal.Snapshot.State.NumDays, save.StartDate ?? context.State.StartDate ?? proposal.Snapshot.State.StartDate),
                            context.State, null, null, "confirm", commit);
                    }
                    else prepared = new(save.Reply!, context.State with { PendingSave = save.Pending }, null, null);
                }
                else
                {
                    prepared = await orchestrator.RunAsync(context, ct);
                    // Lời chào hay câu ngoài phạm vi không trả lời câu hỏi lưu nhưng cũng không hủy nó.
                    if (pendingSave is not null && prepared.Action is "chat" or "food_info" or "out_of_scope")
                        prepared = prepared with { State = prepared.State with { PendingSave = pendingSave } };
                }
                if (prepared.Action == "reset") prepared = prepared with { State = await ResetStateAsync(conversation, message.SequenceNo, ct) };
                else if (prepared.Action == "discard") prepared = prepared with
                    { State = context.State with { PendingQuestion = null, PendingStateChange = null, PendingPlanningRequest = null, PlanningIntake = null, FocusDay = null, FocusMeal = null } };
            }
            // STEP-32: When context exists and prepared.Snapshot exists, set prepared.
            // Mọi đường tạo bản nháp, kể cả không qua model, đều đi qua đây nên phần giới thiệu luôn khớp với món cuối cùng.
            if (context is not null && prepared.Snapshot is { } drafted) prepared = prepared with { Snapshot = AgentGroundedReply.AttachNotes(drafted, context) };
            // STEP-33: Return the result of FinishAsync.
            // Giai đoạn 3: nhận lại khóa và kiểm tra quyền ghi trước khi lưu trọn kết quả một lượt.
            return await FinishAsync(conversation.Id, userId, message, attemptId, claimedVersion, request, context, prepared, ct);
        }
        catch (Exception error)
        {
            // STEP-34: On Exception, call telemetry.Record, call FailAsync and 1 more action(s).
            telemetry.Record("turn_failed", id, message.TurnId, attemptId, new
                { errorCode = (error as AppException)?.ErrorCode, errorType = error.GetType().Name });
            await FailAsync(id, userId, message, attemptId);
            throw;
        }
    }

    private async Task<AgentState> ResetStateAsync(AgentConversation conversation, long sequence, CancellationToken ct)
    {
        // STEP-01: Prepare roadmap from roadmaps.GetOwnedDetailAsync and state from AgentContextBuilder.StateFromRoadmap.
        // Roadmap đã bị xóa: "làm lại từ đầu" là lối thoát duy nhất của hội thoại này, nên bắt đầu trống thay vì trả 404; FinishAsync gỡ liên kết.
        var roadmap = conversation.RoadmapId is { } id ? await roadmaps.GetOwnedDetailAsync(id, conversation.UserId, ct) : null;
        var state = roadmap is not null ? AgentContextBuilder.StateFromRoadmap(roadmap) : new AgentState();
        // STEP-02: Return a copy of state with updated fields.
        return state with { HistoryStartSequence = sequence };
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
        AgentProposal? created = null;
        AgentCommitDto? commitResult = null;
        // STEP-07: When prepared.Commit exists, prepare committed through committer.CommitLockedAsync, set commitResult and 1 more action(s).
        if (prepared.Commit is { } commit)
        {
            var committed = await committer.CommitLockedAsync(conversation, commit, userId, true, ct);
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
        if (previous is not null && prepared.Snapshot is null && prepared.Commit is null &&
            prepared.Action is null && prepared.State.PendingQuestion is not null)
            prepared = prepared with { Reply = prepared.Reply + "\nBản đề xuất trước đó được giữ lại; chưa có lịch cập nhật cho yêu cầu này." };
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
