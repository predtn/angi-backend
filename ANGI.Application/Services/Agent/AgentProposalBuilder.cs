using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.UseCases.Validators.Agent;

namespace ANGI.Application.Services.Agent;

/// <summary>Builds immutable snapshots and enforces deterministic scope and source-item identity.</summary>
public sealed class AgentProposalBuilder
{
    /// <summary>Applies only allowlisted deltas and preserves every item outside the effective mutation scope.</summary>
    public AgentSnapshot Build(AgentContext context, AgentOutput output)
    {
        ValidateScope(context, output.Scope);
        var source = context.Source;
        var original = source?.Items ?? [];
        var items = original.ToList();
        if (output.Action == "create_proposal")
        {
            if (source is not null && context.Request.Command != "alternative" && !context.AlternativeScopes.Any(scope => scope.Covers(output.Scope)) &&
                !(context.ReplanAfterConfirmation && output.Scope == new AgentScope()))
                throw Invalid("Hãy sửa proposal bằng các thay đổi có phạm vi.");
            if (output.Items is null || output.Items.Length == 0 || output.Changes is null || output.Changes.Length != 0) throw Invalid("Lịch đề xuất không hợp lệ.");
            if (output.Items.Any(item => !output.Scope.Contains(item))) throw Invalid("Món nằm ngoài phạm vi yêu cầu.");
            items.RemoveAll(item => output.Scope.Contains(item));
            items.AddRange(output.Items.Select(item => Clean(item, null)));
        }
        else if (output.Action == "patch_proposal")
        {
            if (source is null) throw Invalid("Không có lịch nguồn để chỉnh sửa; dùng create_proposal cho lịch mới.");
            if (output.Changes is null || output.Changes.Length == 0 || output.Items is null || output.Items.Length != 0)
                throw Invalid("patch_proposal phải có changes không rỗng và items=[]; mỗi change gồm type, targetItemKey và item. Backend tự ghép thành proposal đầy đủ.");
            if (context.ProposalVersion.HasValue && output.BaseProposalVersion != context.ProposalVersion)
                throw new ConflictException("PROPOSAL_VERSION_CONFLICT", "Phiên bản đề xuất đã thay đổi.");
            var touched = new HashSet<string>();
            foreach (var change in output.Changes)
            {
                if (change is null || change.Type is not ("add" or "replace" or "remove" or "move")) throw Invalid("Thao tác sửa không hợp lệ.");
                var target = change.TargetItemKey is null ? null : items.SingleOrDefault(x => x.ItemKey == change.TargetItemKey);
                if (change.Type == "add")
                {
                    if (context.Request.SelectedItemKey is not null || change.TargetItemKey is not null || change.Item is null || !output.Scope.Contains(change.Item)) throw Invalid("Món thêm không hợp lệ.");
                    items.Add(Clean(change.Item, null));
                    continue;
                }
                if (target is null || !output.Scope.Contains(target) || !touched.Add(target.ItemKey)) throw Invalid("Tham chiếu món không rõ hoặc ngoài phạm vi.");
                if (context.Request.SelectedItemKey is { } selected && selected != target.ItemKey) throw Invalid("Món sửa khác lựa chọn đang xác nhận.");
                items.Remove(target);
                if (change.Type == "remove")
                {
                    if (change.Item is not null) throw Invalid("Thao tác xóa không nhận món mới.");
                    continue;
                }
                if (change.Item is null || !output.Scope.Contains(change.Item)) throw Invalid("Món thay thế ngoài phạm vi.");
                if (change.Type == "move" && change.Item.DishId != target.DishId) throw Invalid("Thao tác di chuyển không được đổi món.");
                // The checked target, rather than a redundant model field, owns identity during replacement or movement.
                items.Add(Clean(change.Item with { ItemKey = target.ItemKey }, target));
            }
        }
        else throw Invalid("Kiểu đề xuất không hợp lệ.");

        var outside = original.Where(item => !output.Scope.Contains(item)).OrderBy(x => x.ItemKey).ToArray();
        if (!outside.SequenceEqual(items.Where(item => !output.Scope.Contains(item)).OrderBy(x => x.ItemKey))) throw Invalid("Các món ngoài phạm vi phải được giữ nguyên.");
        if (items.Count > 720 || items.Any(x => string.IsNullOrWhiteSpace(x.ItemKey) || x.ItemKey.Length > 100) || items.Select(x => x.ItemKey).Distinct().Count() != items.Count)
            throw Invalid("Khóa món bị trùng hoặc số món vượt giới hạn.");
        if (context.Request.Command == "alternative")
        {
            var excluded = original.Where(output.Scope.Contains).Select(x => x.DishId).ToHashSet();
            if (items.Where(output.Scope.Contains).Any(x => excluded.Contains(x.DishId))) throw Invalid("Chưa đủ món để tạo phương án khác; cần làm rõ điều kiện.");
        }
        foreach (var alternative in context.AlternativeScopes)
        {
            var excluded = original.Where(alternative.Contains).Select(item => item.DishId).ToHashSet();
            // Candidate search scope is read-only: it must not force replacing untouched meals or side dishes.
            var replacements = items.Where(item => alternative.Contains(item) && output.Scope.Contains(item) &&
                original.All(old => old.ItemKey != item.ItemKey || old.DishId != item.DishId)).ToArray();
            if (replacements.Length == 0 || replacements.Any(item => excluded.Contains(item.DishId)))
                throw Invalid("Phương án thay thế cần có món mới khác món cũ; giữ nguyên các món không được yêu cầu sửa.");
        }
        // Keep authorization for selected items; full issued candidate sets remain in the durable tool transcript.
        var grants = (source?.Grants ?? []).Concat(context.Grants).Select(grant => grant with
        {
            DishIds = grant.DishIds.Where(id => items.Any(item => item.DishId == id &&
                (!grant.DayNumber.HasValue || grant.DayNumber == item.DayNumber) && (grant.MealSlot is null || grant.MealSlot == item.MealSlot))).ToArray()
        }).Where(grant => grant.DishIds.Length > 0).ToArray();
        return new AgentSnapshot(context.State, AgentJson.OrderItems(items), grants,
            context.Roadmap?.Id, context.Roadmap?.Version, Delta(original, items), output.Scope,
            original.Where(old => items.Any(item => item.ItemKey == old.ItemKey && item.DishId == old.DishId)).ToArray());
    }

    /// <summary>Builds a backend-derived delta from validated source identity instead of storing untrusted model metadata.</summary>
    private static AgentChange[] Delta(AgentItem[] original, List<AgentItem> items)
    {
        var changes = original.Where(old => items.All(item => item.ItemKey != old.ItemKey))
            .Select(old => new AgentChange("remove", old.ItemKey, null)).ToList();
        foreach (var item in items)
        {
            var previous = original.SingleOrDefault(old => old.ItemKey == item.ItemKey);
            if (previous is null) changes.Add(new("add", null, item));
            else if (previous.DishId != item.DishId) changes.Add(new("replace", item.ItemKey, item));
            else if (previous.DayNumber != item.DayNumber || previous.MealSlot != item.MealSlot || previous.SortOrder != item.SortOrder)
                changes.Add(new("move", item.ItemKey, item));
        }
        return changes.ToArray();
    }

    /// <summary>Checks day/meal scope and binds UI-selected focus to the backend mutation boundary.</summary>
    public static void ValidateScope(AgentContext context, AgentScope scope)
    {
        var requested = context.Request.ConfirmedState;
        if (requested?.FocusDay is { } requestedDay && (scope is null || scope.DayNumber != requestedDay ||
            requested.FocusMeal is not null && scope.MealSlot != requested.FocusMeal))
            throw Invalid("Phạm vi khác ngày hoặc bữa đã chọn trong yêu cầu.");
        if (scope is null || scope.DayNumber.HasValue && (!context.State.NumDays.HasValue || scope.DayNumber < 1 || scope.DayNumber > context.State.NumDays) ||
            scope.MealSlot is not null && (!scope.DayNumber.HasValue || !AgentStateValidator.Meals.Contains(scope.MealSlot))) throw Invalid("Phạm vi chỉnh sửa không hợp lệ.");
        if (context.State.FocusDay.HasValue && (scope.DayNumber != context.State.FocusDay ||
            context.State.FocusMeal is not null && scope.MealSlot != context.State.FocusMeal)) throw Invalid("Phạm vi khác ngày hoặc bữa đã chọn.");
        if (context.Request.SelectedItemKey is { } selected)
        {
            var item = context.Source?.Items.SingleOrDefault(x => x.ItemKey == selected);
            if (item is null || !scope.Contains(item)) throw Invalid("Không tìm thấy món đã chọn trong phạm vi.");
        }
    }

    /// <summary>Discards model-supplied provenance and retains feedback identity only for an unchanged dish.</summary>
    private static AgentItem Clean(AgentItem item, AgentItem? source) => new(item.ItemKey,
        source?.DishId == item.DishId ? source.SourceItemId : null, item.DayNumber, item.MealSlot, item.SortOrder,
        item.DishId, source?.DishId == item.DishId ? source.RequestId : null);

    /// <summary>Creates the documented deterministic rejection without changing any persisted proposal.</summary>
    private static ConflictException Invalid(string message) => new("PROPOSAL_NOT_COMMITTABLE", message);
}
