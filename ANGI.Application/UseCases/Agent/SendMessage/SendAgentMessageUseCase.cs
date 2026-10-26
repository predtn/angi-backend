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
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentValidation;

namespace ANGI.Application.UseCases.Agent.SendMessage;

/// <summary>Implements AGENT-02 with a short claim transaction, external execution and fenced finalization.</summary>
public sealed class SendAgentMessageUseCase(IValidator<SendAgentMessageRequestDto> validator, IAgentRepository repository,
    IRoadmapRepository roadmaps, ICurrentUserService user, IUnitOfWork unitOfWork, TimeProvider clock,
    AgentLimits limits, AgentContextBuilder contextBuilder, AgentOrchestrator orchestrator, IAgentTelemetry telemetry) : ISendAgentMessageUseCase
{
    /// <summary>Replays completed requests before version checks and prevents late or concurrent attempts from writing state.</summary>
    public async Task<AgentTurnDto> ExecuteAsync(long id, SendAgentMessageRequestDto request, CancellationToken ct)
    {
        // id là conversationId. Roadmap cần sửa được xác định qua conversation.RoadmapId,
        // do frontend truyền khi mở conversation; không suy ra quyền truy cập từ ID viết trong prompt.
        await validator.ValidateAndThrowAsync(request, ct);
        AgentConversationRules.ValidateId(id);
        var userId = AgentConversationRules.UserId(user);
        var hash = Hash(request);
        AgentConversation conversation;
        AgentMessage message;
        long attemptId;
        // Giai đoạn 1: transaction ngắn để kiểm tra và nhận quyền xử lý (claim).
        // Lease là quyền tạm thời gồm turnId, attemptId và thời điểm hết hạn; chưa gọi LLM ở đây.
        await using (var transaction = await unitOfWork.BeginTransactionAsync(ct))
        {
            // Khóa conversation đúng chủ để hai request không đồng thời nhận quyền sửa cùng state.
            conversation = await repository.LockOwnedAsync(id, userId, ct) ?? throw AgentConversationRules.Missing();
            var now = clock.GetUtcNow().UtcDateTime;
            var existing = await repository.GetMessageAsync(id, request.ClientMessageId, ct);
            // Một clientMessageId chỉ đại diện cho một nội dung nghiệp vụ trong conversation.
            // Nếu đã completed, trả nguyên response đã lưu trước kiểm tra expiry/version/quota:
            // retry do mất mạng không gọi lại LLM/Recommendation hoặc tạo proposal thứ hai.
            if (existing is not null && existing.RequestHash != hash) throw new ConflictException("MESSAGE_ID_REUSED", "ID tin nhắn đã được dùng cho nội dung khác.");
            if (existing?.ProcessingStatus == AgentProcessingStatus.Completed && existing.ResponsePayload is not null)
                return AgentJson.Read<AgentTurnDto>(existing.ResponsePayload) with { Replayed = true };
            AgentConversationRules.RequireActive(conversation, now);
            // Retry chính tin nhắn đang chạy nhận trạng thái processing (controller trả 202).
            // Tin nhắn khác phải chờ, tránh hai lượt chat ghi đè state/proposal của nhau.
            if (AgentConversationRules.Busy(conversation, now))
            {
                if (existing is not null && existing.TurnId == conversation.ProcessingTurnId) return new("processing", "Tin nhắn đang được xử lý.", conversation.Version);
                throw new ConflictException("CONVERSATION_BUSY", "Hội thoại đang xử lý một tin nhắn khác.");
            }
            // Client phải gửi version đã đọc gần nhất; version cũ cần reload trước khi chạy lượt mới.
            if (request.ExpectedConversationVersion != conversation.Version) throw new ConflictException("CONVERSATION_VERSION_CONFLICT", "Hãy tải lại hội thoại trước khi gửi tin nhắn.");
            // Khóa theo user vì quota dùng chung cho mọi conversation của user, kể cả retry failed.
            await repository.LockUserAsync(userId, ct);
            if (await repository.CountAttemptsAsync(userId, now.AddMinutes(-1), ct) >= limits.MessagesPerMinute) throw new AgentRateLimitedException(60);
            if (await repository.CountAttemptsAsync(userId, now.AddDays(-1), ct) >= limits.MessagesPerDay) throw new AgentRateLimitedException(86400);
            if (conversation.ProcessingTurnId is { } oldTurn)
            {
                // Đã qua kiểm tra Busy: lease này đã hết hạn, lượt cũ được đánh dấu failed trước takeover.
                var abandoned = await repository.GetTurnAsync(id, oldTurn, ct);
                if (abandoned is not null) abandoned.ProcessingStatus = AgentProcessingStatus.Failed;
            }
            // Retry failed tái sử dụng logical message cũ; message mới mới được cấp sequence/turn mới.
            if (existing is null)
            {
                var sequence = await repository.NextSequenceAsync(id, ct);
                message = new AgentMessage { ConversationId = id, SequenceNo = sequence, TurnId = sequence,
                    ClientMessageId = request.ClientMessageId, Role = AgentMessageRole.User, RequestHash = hash };
            }
            else message = existing;
            // Mỗi lần thử có attemptId tăng dần, dù vẫn là cùng clientMessageId/turnId.
            // FinishAsync dùng ID này để chặn worker cũ trả về muộn sau khi lượt khác đã tiếp quản.
            attemptId = checked(conversation.Version + 1);
            var previousAttempts = existing is null ? [] : AgentJson.Read<AgentStoredMessage>(existing.Payload).Attempts ?? [];
            message.Payload = AgentJson.Write(new AgentStoredMessage(request.Content.Trim(), previousAttempts.Append(now).ToArray(),
                request.SelectedItemKey, request.Command, request.ConfirmedState));
            message.ProcessingStatus = AgentProcessingStatus.Processing;
            if (existing is null) repository.AddMessage(message);
            conversation.ProcessingTurnId = message.TurnId;
            conversation.ProcessingAttemptId = attemptId;
            conversation.ProcessingUntil = now.AddSeconds(limits.LeaseSeconds);
            conversation.Version = attemptId;
            conversation.ExpiresAt = now.AddDays(limits.ActiveDays);
            await unitOfWork.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        // Giai đoạn 2: đã thả khóa DB; các lời gọi LLM, Recommendation, Map chạy bên ngoài transaction.
        var claimedVersion = conversation.Version;
        telemetry.Record("claimed", id, message.TurnId, attemptId);
        try
        {
            AgentPreparedTurn prepared;
            AgentContext? context = null;
            if (request.Command is "discard" or "reset")
            {
                // Lệnh rõ ràng từ request được xử lý trực tiếp, không cần LLM diễn giải.
                // discard giữ yêu cầu chuyến đi; reset lấy lại state từ roadmap đã gắn hoặc state trống.
                // Cả hai chỉ bỏ proposal, không xóa hoặc sửa roadmap chính thức.
                var state = AgentJson.Read<AgentState>(conversation.State);
                if (request.Command == "reset")
                {
                    state = conversation.RoadmapId is { } roadmapId ? AgentContextBuilder.StateFromRoadmap(
                        await roadmaps.GetOwnedDetailAsync(roadmapId, userId, ct) ?? throw new NotFoundException("ROADMAP_NOT_FOUND", "Không tìm thấy roadmap.")) : new AgentState();
                }
                prepared = new("Đã bỏ bản đề xuất đang chỉnh sửa. Bạn có thể tiếp tục lên kế hoạch.", state with { PendingQuestion = null, FocusDay = null, FocusMeal = null }, null, null);
            }
            else
            {
                // Nạp state, proposal hiện hành, roadmap mới nhất (nếu có) và history completed.
                // Ví dụ prompt chỉ nói "sửa roadmap cho tôi": model được hướng dẫn hỏi rõ phần cần sửa.
                // ContextBuilder chỉ nạp roadmap đã gắn; luồng này chưa có tool tìm/chọn roadmap khác.
                context = await contextBuilder.BuildAsync(conversation, request, ct);
                context.TurnId = message.TurnId;
                context.AttemptId = attemptId;
                // Orchestrator gọi LLM/tools rồi build + validate; kết quả ở đây vẫn chưa được lưu.
                prepared = await orchestrator.RunAsync(context, ct);
            }
            // Giai đoạn 3: nhận lại khóa và kiểm tra quyền ghi trước khi lưu trọn kết quả một lượt.
            return await FinishAsync(conversation.Id, userId, message, attemptId, claimedVersion, request, context, prepared, ct);
        }
        catch (Exception error)
        {
            telemetry.Record("turn_failed", id, message.TurnId, attemptId, new
                { errorCode = (error as AppException)?.ErrorCode, errorType = error.GetType().Name });
            await FailAsync(id, userId, message, attemptId);
            throw;
        }
    }

    /// <summary>Stores a result only while the same unexpired attempt still owns the claimed conversation revision.</summary>
    private async Task<AgentTurnDto> FinishAsync(long id, int userId, AgentMessage message, long attemptId, long claimedVersion,
        SendAgentMessageRequestDto request, AgentContext? context, AgentPreparedTurn prepared, CancellationToken ct)
    {
        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);
        var conversation = await repository.LockOwnedAsync(id, userId, ct) ?? throw AgentConversationRules.Missing();
        var now = clock.GetUtcNow().UtcDateTime;
        // Chỉ attempt đã claim mới được ghi, khi lease chưa hết và conversation chưa đổi version.
        // LLM trả kết quả thành công không đủ để cho phép một worker đã mất quyền ghi vào DB.
        if (conversation.ProcessingAttemptId != attemptId || conversation.ProcessingUntil <= now || conversation.Version != claimedVersion)
            throw new ConflictException("CONVERSATION_VERSION_CONFLICT", "Lượt xử lý đã hết quyền ghi. Hãy tải lại hội thoại.");
        AgentConversationRules.RequireActive(conversation, now);
        if (context?.Roadmap is { } sourceRoadmap)
        {
            // Trong lúc gọi dịch vụ ngoài, người dùng hoặc conversation khác có thể đã sửa roadmap.
            // Chặn lưu proposal dựa trên snapshot cũ thay vì âm thầm bỏ qua chỉnh sửa mới.
            var currentRoadmap = await roadmaps.LockAndGetTrackedAsync(sourceRoadmap.Id, userId, ct);
            if (currentRoadmap is null || currentRoadmap.Version != sourceRoadmap.Version)
                throw new ConflictException("ROADMAP_VERSION_CONFLICT", "Roadmap đã thay đổi trong lúc xử lý.");
        }
        var previous = conversation.CurrentProposalId is { } proposalId ? await repository.GetProposalAsync(id, proposalId, ct) : null;
        AgentProposal? created = null;
        if (request.Command is "discard" or "reset")
        {
            // Bỏ bản nháp hiện hành; bản lịch chính thức không bị thay đổi.
            if (previous?.Status == AgentProposalStatus.Ready) previous.Status = AgentProposalStatus.Discarded;
            conversation.CurrentProposalId = null;
            conversation.CurrentProposal = null;
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
                BaseRoadmapId = prepared.Snapshot.BaseRoadmapId, BaseRoadmapVersion = prepared.Snapshot.BaseRoadmapVersion,
                Payload = AgentJson.Write(prepared.Snapshot), ValidationResult = AgentJson.Write(prepared.Validation),
                Status = AgentProposalStatus.Ready, ExpiresAt = now.AddDays(limits.ActiveDays),
                RecoRequestIds = prepared.Snapshot.Items.Where(x => x.RequestId.HasValue).Select(x => x.RequestId!.Value).Distinct().ToArray()
            };
            repository.AddProposal(created);
            conversation.CurrentProposalId = created.Id;
            conversation.CurrentProposal = created;
        }
        // clarify/answer hoặc validation có lỗi không tạo snapshot mới: giữ proposal cũ để xem lại.
        // State mới vẫn được lưu; Confirm sẽ kiểm tra nó có còn khớp proposal trước khi cho commit.
        conversation.State = AgentJson.Write(prepared.State);
        conversation.Version++;
        conversation.ExpiresAt = now.AddDays(limits.ActiveDays);
        AgentConversationRules.ClearLease(conversation);
        var result = new AgentTurnDto("completed", prepared.Reply, conversation.Version,
            created is not null ? AgentContextBuilder.MapProposal(created) : previous is null || request.Command is "discard" or "reset" ? null : AgentContextBuilder.MapProposal(previous), prepared.Validation);
        // Lưu response cùng logical user message để request gửi lại có thể replay chính xác kết quả này.
        message.ProcessingStatus = AgentProcessingStatus.Completed;
        message.ResponsePayload = AgentJson.Write(result);
        var sequence = await repository.NextSequenceAsync(id, ct);
        if (context is not null)
        {
            // Lưu call/result của tools để truy vết nguồn món; không đưa attempt dở vào history completed.
            foreach (var exchange in context.Exchanges)
                repository.AddMessage(new AgentMessage { ConversationId = id, SequenceNo = sequence++, TurnId = message.TurnId,
                    Role = AgentMessageRole.Tool, Payload = AgentJson.Write(new AgentStoredMessage(AgentJson.Write(exchange))) });
        }
        repository.AddMessage(new AgentMessage { ConversationId = id, SequenceNo = sequence, TurnId = message.TurnId,
            Role = AgentMessageRole.Assistant, Payload = AgentJson.Write(new AgentStoredMessage(prepared.Reply)) });
        // Proposal, state, transcript và response replay cùng thành công hoặc cùng rollback.
        // Hàm này chỉ lưu dữ liệu Agent; ghi roadmap thuộc ConfirmAgentProposalUseCase.
        await unitOfWork.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        telemetry.Record("completed", id, message.TurnId, attemptId, new { proposalVersion = created?.ProposalVersion });
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
