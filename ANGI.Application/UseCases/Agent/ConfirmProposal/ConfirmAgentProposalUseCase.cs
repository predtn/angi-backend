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

namespace ANGI.Application.UseCases.Agent.ConfirmProposal;

/// <summary>Implements AGENT-04 with exact proposal confirmation and one transactional business effect.</summary>
public sealed class ConfirmAgentProposalUseCase(IValidator<ConfirmAgentProposalRequestDto> validator,
    IAgentRepository repository, IRoadmapRepository roadmaps, ICurrentUserService user, IUnitOfWork unitOfWork,
    AgentProposalValidator proposalValidator, TimeProvider clock, AgentLimits limits, IAgentTelemetry telemetry) : IConfirmAgentProposalUseCase
{
    /// <summary>Checks routes before locking, rechecks catalog/version under locks and replays already committed results.</summary>
    public async Task<AgentCommitDto> ExecuteAsync(long id, ConfirmAgentProposalRequestDto request, CancellationToken ct)
    {
        // Request phải chỉ rõ proposalId + proposalVersion mà người dùng đã xem và chọn áp dụng.
        // Đây là endpoint xác nhận riêng: không gọi LLM, không diễn giải câu "đồng ý" trong chat.
        await validator.ValidateAndThrowAsync(request, ct);
        try
        {
            var result = await ConfirmAsync(id, request, ct);
            telemetry.Record("confirm_result", id, null, null, new { result.RoadmapId, result.RoadmapVersion, result.CommittedProposalId });
            return result;
        }
        catch (Exception error)
        {
            // Ghi mã lỗi/validation để truy vết; middleware chuyển exception thành response API phù hợp.
            telemetry.Record("commit_failed", id, null, null, new { request.ProposalId, request.ProposalVersion,
                errorCode = (error as AppException)?.ErrorCode, errorType = error.GetType().Name,
                validationErrors = (error as AgentProposalValidationException)?.Validation.Errors });
            throw;
        }
    }

    /// <summary>Performs preflight outside the transaction and then commits under conversation, roadmap and catalog locks.</summary>
    private async Task<AgentCommitDto> ConfirmAsync(long id, ConfirmAgentProposalRequestDto request, CancellationToken ct)
    {
        // Giai đoạn 1: đọc và kiểm tra trước transaction để không giữ khóa khi phải chờ Map adapter.
        // id là conversationId; proposal phải thuộc conversation đã kiểm tra đúng chủ này.
        AgentConversationRules.ValidateId(id);
        var userId = AgentConversationRules.UserId(user);
        var conversation = await repository.GetOwnedAsync(id, userId, ct) ?? throw AgentConversationRules.Missing();
        var proposal = await repository.GetProposalSnapshotAsync(id, request.ProposalId, ct) ?? throw NotCommittable();
        RequireVersion(proposal, request);
        // Người dùng bấm xác nhận lại sau mất mạng: trả kết quả commit cũ, không áp dụng lần thứ hai.
        // Kiểm tra replay trước expiry/current-proposal vì hiệu ứng nghiệp vụ đã hoàn tất trước đó.
        if (proposal.Status == AgentProposalStatus.Committed) return AgentJson.Read<AgentCommitDto>(proposal.CommitResult!);
        RequireCommittable(conversation, proposal, clock.GetUtcNow().UtcDateTime);
        var snapshot = AgentJson.Read<AgentSnapshot>(proposal.Payload);
        var source = snapshot.BaseRoadmapId is { } sourceId
            ? await roadmaps.GetOwnedDetailAsync(sourceId, userId, ct) ?? throw new NotFoundException("ROADMAP_NOT_FOUND", "Không tìm thấy roadmap.") : null;
        // Proposal update gắn với một version roadmap cụ thể. Nếu đã sửa tay hoặc sửa từ chat khác,
        // yêu cầu tạo proposal mới để người dùng xem lại, không tự ghép lên phiên bản mới.
        if (source is not null && source.Version != snapshot.BaseRoadmapVersion) throw VersionConflict();
        var sourceSnapshot = source is null ? null : AgentJson.FromRoadmap(source, snapshot.State);
        // confirm=true: kiểm tra lại cả giá so với preview; calculateRoutes=true: tính tuyến ngoài khóa DB.
        var outside = await proposalValidator.ValidateAsync(snapshot, sourceSnapshot, true, true, ct);
        if (outside.Validation.Errors.Length > 0) throw new AgentProposalValidationException(outside.Validation);

        // Giai đoạn 2: thứ tự khóa conversation -> roadmap nguồn -> catalog để bảo vệ lần ghi cuối.
        // Nạp lại proposal và kiểm tra replay vì request confirm khác có thể vừa commit sau preflight.
        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);
        conversation = await repository.LockOwnedAsync(id, userId, ct) ?? throw AgentConversationRules.Missing();
        proposal = await repository.GetProposalAsync(id, request.ProposalId, ct) ?? throw NotCommittable();
        RequireVersion(proposal, request);
        if (proposal.Status == AgentProposalStatus.Committed) return AgentJson.Read<AgentCommitDto>(proposal.CommitResult!);
        var now = clock.GetUtcNow().UtcDateTime;
        RequireCommittable(conversation, proposal, now);
        Domain.Entities.Roadmap roadmap;
        if (snapshot.BaseRoadmapId is { } roadmapId)
        {
            // Update: giữ nguyên roadmapId và so sánh lại version dưới khóa trước khi thay đổi dữ liệu.
            roadmap = await roadmaps.LockAndGetTrackedAsync(roadmapId, userId, ct)
                ?? throw new NotFoundException("ROADMAP_NOT_FOUND", "Không tìm thấy roadmap.");
            if (roadmap.Version != snapshot.BaseRoadmapVersion) throw VersionConflict();
            sourceSnapshot = AgentJson.FromRoadmap(roadmap, snapshot.State);
        }
        else
        {
            // Create: tới bước xác nhận này mới tạo roadmap chính thức; chat trước đó chỉ có proposal.
            roadmap = new Domain.Entities.Roadmap { UserId = userId };
            roadmaps.Add(roadmap);
        }
        // Giá/trạng thái có thể đổi trong lúc preflight: khóa catalog rồi kiểm tra lại ngay trong transaction.
        // calculateRoutes=false để không gọi dịch vụ bản đồ trong khi đang giữ khóa.
        await repository.LockCatalogAsync(snapshot.Items.Select(x => x.DishId).Distinct().ToArray(), ct);
        var current = await proposalValidator.ValidateAsync(snapshot, sourceSnapshot, true, false, ct);
        if (current.Validation.Errors.Length > 0) throw new AgentProposalValidationException(current.Validation);
        // Áp dụng snapshot đã xác minh vào entity đang tracking; chưa trả thành công trước khi commit.
        var mapping = Apply(roadmap, current.Snapshot);
        if (conversation.ProcessingTurnId is { } abandonedTurn)
        {
            // RequireCommittable đã chặn live lease; nếu còn turn ở đây thì đó là lease cũ đã hết hạn.
            var abandoned = await repository.GetTurnAsync(id, abandonedTurn, ct);
            if (abandoned is not null) abandoned.ProcessingStatus = AgentProcessingStatus.Failed;
        }
        AgentConversationRules.ClearLease(conversation);
        // Flush lần 1 lấy ID roadmap/day/item và version do trigger DB quản lý.
        // SaveChanges chưa phải commit: nếu các bước sau lỗi, toàn bộ transaction vẫn rollback.
        await unitOfWork.SaveChangesAsync(ct);
        var version = await repository.GetRoadmapVersionAsync(roadmap.Id, ct);
        var result = new AgentCommitDto(roadmap.Id, version, proposal.Id, mapping.ToDictionary(x => x.Key, x => x.Value.Id));
        // Lưu nguyên kết quả commit, kể cả ánh xạ itemKey -> itemId, để confirm lặp có thể replay.
        proposal.Status = AgentProposalStatus.Committed;
        proposal.ResultRoadmapId = roadmap.Id;
        proposal.ResultRoadmapVersion = version;
        proposal.CommitResult = AgentJson.Write(result);
        proposal.CommittedAt = now;
        conversation.RoadmapId = roadmap.Id;
        conversation.State = AgentJson.Write(snapshot.State with { FocusDay = null, FocusMeal = null });
        conversation.Version++;
        conversation.ExpiresAt = now.AddDays(limits.ActiveDays);
        // Flush lần 2 lưu trạng thái Committed và state mới trong cùng transaction với roadmap.
        // Chỉ sau CommitAsync mới trả roadmapId cho frontend tải/mở lịch chính thức.
        await unitOfWork.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        telemetry.Record("committed", id, null, null, new { proposal.ProposalVersion, result.RoadmapId, result.RoadmapVersion });
        return result;
    }

    /// <summary>Applies a validated full snapshot while retaining item identity only when its dish is unchanged.</summary>
    private Dictionary<string, RoadmapItem> Apply(Domain.Entities.Roadmap roadmap, AgentSnapshot snapshot)
    {
        // Chép metadata đã được kiểm tra từ snapshot, không tạo lại lịch bằng nội dung chat hoặc gọi LLM.
        var state = snapshot.State;
        roadmap.Title = state.Title.Trim();
        roadmap.StartDate = state.StartDate;
        roadmap.NumDays = state.NumDays!.Value;
        roadmap.BudgetAmount = state.BudgetAmount;
        roadmap.OriginMode = state.OriginMode switch { "pinned" => RoadmapOriginMode.Pinned, "gps" => RoadmapOriginMode.Gps, _ => RoadmapOriginMode.None };
        roadmap.OriginLatitude = state.OriginMode == "pinned" ? state.OriginLatitude : null;
        roadmap.OriginLongitude = state.OriginMode == "pinned" ? state.OriginLongitude : null;
        roadmap.OriginLabel = state.OriginMode == "pinned" ? state.OriginLabel : null;
        // SourceItemId chỉ được giữ khi vẫn là món nguồn. Item không còn trong snapshot sẽ bị loại bỏ.
        // Đổi món phải tạo item mới theo RM-08 để feedback của món cũ không đi theo món thay thế.
        var existing = roadmap.Days.SelectMany(x => x.Items).ToDictionary(x => x.Id);
        var retained = snapshot.Items.Where(x => x.SourceItemId.HasValue).Select(x => x.SourceItemId!.Value).ToHashSet();
        foreach (var item in existing.Values.Where(x => !retained.Contains(x.Id))) roadmaps.RemoveItem(item);
        // Đồng bộ số ngày; một update xóa hết món vẫn giữ roadmap và các ngày thuộc chuyến đi.
        foreach (var day in roadmap.Days.Where(x => x.DayNumber > roadmap.NumDays).ToArray()) roadmap.Days.Remove(day);
        for (short day = 1; day <= roadmap.NumDays; day++)
            if (roadmap.Days.All(x => x.DayNumber != day)) roadmap.Days.Add(new RoadmapDay { DayNumber = day, Roadmap = roadmap });
        var mapping = new Dictionary<string, RoadmapItem>();
        foreach (var item in snapshot.Items)
        {
            var day = roadmap.Days.Single(x => x.DayNumber == item.DayNumber);
            // Giữ ID khi chỉ di chuyển món cũ; món mới hoặc món thay thế được cấp RoadmapItem.Id mới.
            var saved = item.SourceItemId is { } id && existing.TryGetValue(id, out var old) && old.DishId == item.DishId ? old : null;
            if (saved is null)
            {
                saved = new RoadmapItem { RoadmapDay = day, DishId = item.DishId, Source = RoadmapItemSource.Ai, RecoRequestId = item.RequestId };
                roadmaps.AddItem(saved);
            }
            saved.RoadmapDay = day;
            saved.MealSlot = Enum.Parse<MealSlot>(item.MealSlot.Replace("_", ""), true);
            saved.SortOrder = item.SortOrder;
            // Frontend dùng mapping này để liên hệ dòng preview với item chính thức sau commit.
            mapping.Add(item.ItemKey, saved);
        }
        return mapping;
    }

    /// <summary>Checks a displayed proposal revision even for an idempotent committed replay.</summary>
    private static void RequireVersion(AgentProposal proposal, ConfirmAgentProposalRequestDto request)
    {
        // ProposalVersion là phiên bản preview, khác với Conversation.Version và Roadmap.Version.
        // Kể cả replay, client vẫn phải xác nhận đúng phiên bản của proposal được yêu cầu.
        if (proposal.ProposalVersion != request.ProposalVersion) throw new ConflictException("PROPOSAL_VERSION_CONFLICT", "Phiên bản đề xuất không khớp.");
    }

    /// <summary>Rejects expired, busy, noncurrent and unreviewed constraint changes before first commit.</summary>
    private static void RequireCommittable(AgentConversation conversation, AgentProposal proposal, DateTime now)
    {
        // Chỉ lần commit đầu mới cần active/không bận/chưa hết hạn/current Ready.
        // Proposal đã Committed được replay ở ConfirmAsync trước khi đi vào các điều kiện này.
        AgentConversationRules.RequireActive(conversation, now);
        if (AgentConversationRules.Busy(conversation, now)) throw new ConflictException("CONVERSATION_BUSY", "Hội thoại đang xử lý tin nhắn.");
        if (proposal.ExpiresAt <= now) throw new GoneException("PROPOSAL_EXPIRED", "Đề xuất đã hết hạn.");
        if (proposal.Status != AgentProposalStatus.Ready || conversation.CurrentProposalId != proposal.Id) throw NotCommittable();
        var state = AgentJson.Read<AgentState>(conversation.State);
        // Có câu hỏi đang chờ hoặc state đã khác ràng buộc trong preview thì phải xem proposal mới.
        // Tránh xác nhận lịch cũ sau khi người dùng vừa đổi ngân sách/tỉnh/bộ lọc trong chat.
        if (state.PendingQuestion is not null || !AgentConversationRules.SameConstraints(state, AgentJson.Read<AgentSnapshot>(proposal.Payload).State)) throw NotCommittable();
    }

    /// <summary>Reports a changed source without rebasing or applying a partial delta.</summary>
    private static ConflictException VersionConflict() => new("ROADMAP_VERSION_CONFLICT", "Roadmap đã thay đổi. Cần tạo đề xuất mới.");

    /// <summary>Reports invalid or other-conversation proposal IDs without leaking their existence.</summary>
    private static ConflictException NotCommittable() => new("PROPOSAL_NOT_COMMITTABLE", "Đề xuất không còn sẵn sàng để xác nhận.");
}
