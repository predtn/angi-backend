using System.Text.RegularExpressions;
using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Services.Agent.Constraints;
using ANGI.Application.Services.Agent.Conversation;
using ANGI.Application.UseCases.Validators.Agent;

namespace ANGI.Application.Services.Agent.Proposals;

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
        var retainedKeys = original.Select(item => item.ItemKey).ToHashSet(StringComparer.Ordinal);
        if (source is not null && output.BaseProposalVersion.HasValue && context.ProposalVersion.HasValue &&
            output.BaseProposalVersion != context.ProposalVersion)
            throw new ConflictException("PROPOSAL_VERSION_CONFLICT", "Phiên bản đề xuất đã thay đổi.");
        if (output.Action == "create_proposal")
        {
            // A complete scoped selection is a payload shape, not permission to discard the current trip.
            // The backend merges it into the owned source and derives the actual delta below.
            if (context.AlternativeScopes.Count > 0 && context.Request.Command != "alternative" &&
                !context.AlternativeScopes.Any(scope => scope.Covers(output.Scope)))
                throw Invalid("Phạm vi đề xuất rộng hơn phạm vi tìm phương án thay thế.");
            // Model đôi khi gửi phạm vi hẹp (ví dụ ngày 4) nhưng chép kèm các món ngày khác y nguyên. Bản sao không đổi đó vô hại:
            // backend vốn giữ nguyên mọi món ngoài phạm vi, nên bỏ chúng thay vì từ chối cả đề xuất. Món ngoài phạm vi bị đổi vẫn bị từ chối.
            if (output.Items is not null)
            {
                var supplied = output.Items.Length;
                output = output with { Items = output.Items.Where(item => output.Scope.Contains(item) || !original.Any(old =>
                    old.DayNumber == item.DayNumber && old.MealSlot == item.MealSlot && old.DishId == item.DishId)).ToArray() };
                // Chỉ chép lại món ngoài phạm vi mà quên món thay thế: nói đúng lỗi để lần sửa duy nhất không phải đoán.
                if (supplied > 0 && output.Items.Length == 0)
                    throw Invalid($"Đề xuất chỉ gồm các món ngoài phạm vi {(output.Scope.DayNumber is { } day ? $"ngày {day}" : "đã chọn")}{(output.Scope.MealSlot is { } slot ? $", bữa {slot}" : "")}. Chỉ gửi món thay thế thuộc đúng phạm vi đó.");
            }
            if (output.Items is null || output.Items.Length == 0 || output.Changes is null || output.Changes.Length != 0) throw Invalid("Lịch đề xuất không hợp lệ.");
            if (output.Items.Where(item => !output.Scope.Contains(item)).Select(item => $"ngày {item.DayNumber} {AgentReplyBuilder.Meal(item.MealSlot)}")
                .Distinct().ToArray() is { Length: > 0 } outOfScope)
                // Phản hồi cụ thể để lần sửa của model biết bỏ món nào, thay vì trả về đề xuất rỗng.
                throw Invalid($"Món nằm ngoài phạm vi yêu cầu ({string.Join(", ", outOfScope)}). Chỉ gửi các món thuộc phạm vi đã chọn; " +
                    "món ngoài phạm vi được giữ nguyên.");
            if (output.Items.Select(item => item.ItemKey).Distinct(StringComparer.Ordinal).Count() != output.Items.Length)
                throw Invalid("Khóa món bị trùng trong đề xuất.");
            var replaced = original.Where(output.Scope.Contains).ToArray();
            if (context.Request.SelectedItemKey is { } selected)
            {
                replaced = original.Where(item => item.ItemKey == selected).ToArray();
                if (output.Items.Length != 1 || output.Items[0].DayNumber != replaced[0].DayNumber ||
                    output.Items[0].MealSlot != replaced[0].MealSlot)
                    throw Invalid("Đề xuất phải chỉ thay món đã chọn trong đúng ngày/bữa.");
            }
            var replacedKeys = replaced.Select(item => item.ItemKey).ToHashSet(StringComparer.Ordinal);
            items.RemoveAll(item => replacedKeys.Contains(item.ItemKey));
            retainedKeys.ExceptWith(replacedKeys);
            var usedKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in output.Items)
            {
                var previous = replaced.FirstOrDefault(old => old.ItemKey == item.ItemKey && !usedKeys.Contains(old.ItemKey))
                    ?? replaced.FirstOrDefault(old => !usedKeys.Contains(old.ItemKey) && old.DayNumber == item.DayNumber &&
                        old.MealSlot == item.MealSlot && old.SortOrder == item.SortOrder);
                if (previous is not null) usedKeys.Add(previous.ItemKey);
                // A full snapshot may copy unaffected rows along with edited days. Only fresh selections
                // consume new ranking provenance; an exact, unaffected copy keeps its original metadata.
                if (previous is not null && source is not null && previous.DishId == item.DishId &&
                    previous.DayNumber == item.DayNumber && previous.MealSlot == item.MealSlot && previous.SortOrder == item.SortOrder &&
                    !AgentScopedPlanning.RequiresRefresh(source.State, context.State, previous) &&
                    !context.Grants.Any(grant => (!grant.DayNumber.HasValue || grant.DayNumber == item.DayNumber) &&
                        (grant.MealSlot is null || grant.MealSlot == item.MealSlot) && grant.DishIds.Contains(item.DishId) &&
                        AgentScopedPlanning.Fingerprint(grant.Constraints, item.DayNumber, item.MealSlot) ==
                        AgentScopedPlanning.Fingerprint(context.State, item.DayNumber, item.MealSlot)))
                {
                    retainedKeys.Add(previous.ItemKey);
                    items.Add(previous);
                    continue;
                }
                items.Add(Clean(previous is null ? item : item with { ItemKey = previous.ItemKey }, previous));
            }
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
                if (change.Type is "remove" or "replace") retainedKeys.Remove(target.ItemKey);
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
        if (items.Count > AgentStateValidator.MaxItems || items.Any(x => string.IsNullOrWhiteSpace(x.ItemKey) || x.ItemKey.Length > 100) || items.Select(x => x.ItemKey).Distinct().Count() != items.Count)
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
            context.Roadmap?.Id, context.Roadmap?.UpdatedAt, Delta(original, items), output.Scope,
            original.Where(old => retainedKeys.Contains(old.ItemKey) &&
                items.Any(item => item.ItemKey == old.ItemKey && item.DishId == old.DishId)).ToArray());
    }

    // Mỗi món trong nguồn gửi cho model tốn khoảng 100 token ở mọi lần gọi; quá mức này chỉ gửi các ngày tin nhắn nhắc tới.
    private const int MaxPromptItems = 60;

    /// <summary>A long plan cut to the days or dishes this message names; null when the whole source fits, so short plans reach the model as before.</summary>
    public static AgentSnapshot? PromptSource(AgentContext context)
    {
        if (context.Source is not { } source || source.Items.Length <= MaxPromptItems) return null;
        var named = AgentMealScopePolicy.Read(context.Request.Content, context.State);
        var days = named.Days.Concat(named.Meals.Select(meal => meal.DayNumber ?? 0)).Concat(context.MovedDays)
            .Concat(source.Items.Where(item => item.ItemKey == context.Request.SelectedItemKey).Select(item => item.DayNumber)).ToHashSet();
        if (context.State.FocusDay is { } focus) days.Add(focus);
        var message = NameWords(context.Request.Content);
        return source with { Items = source.Items.Where(item => days.Contains(item.DayNumber) || item.DishName is not null && message.Contains(NameWords(item.DishName), StringComparison.Ordinal)).ToArray(),
            Grants = [], Changes = null, RetainedItems = null, DishNotes = null, DaySummaries = null };
    }

    // Khớp nguyên từ: "Dish 2" không được khớp "Dish 25".
    private static string NameWords(string text) => $" {Regex.Replace(AgentScopedPlanning.Normalize(text)!, @"[^\p{L}\p{M}\p{N}]+", " ").Trim()} ";

    /// <summary>A long trip whose whole plan this turn searched for: the backend assembles it, because the model could not write that many items in one reply.</summary>
    public static bool AssemblesWholeTrip(AgentContext context)
    {
        if (!AgentScopedPlanning.IsLongTrip(context.State) || context.Grants.Count == 0 || context.Exchanges.Count == 0 ||
            !context.Exchanges[^1].Calls.Any(call => call.Name == "get_recommendation" && call.Arguments.TryGetProperty("scope", out var scope) && scope.GetString() == "full")) return false;
        var named = AgentMealScopePolicy.Read(context.Request.Content, context.State);
        return context.Source is not { Items.Length: > 0 } || named.Days.Length == 0 && named.Meals.Length == 0 && !context.ItineraryChanged;
    }

    /// <summary>
    /// Fills a new plan from the candidates already issued this turn, best ranked first, when the model could not submit a usable one.
    /// Null when an existing plan or a narrowed scope is involved, or when a position has no candidate; validation still checks the result.
    /// A long trip may replace an existing plan and also draws on the reserve list; when every candidate of a meal is used, the least used comes back.
    /// </summary>
    public static AgentOutput? Assemble(AgentContext context, bool longTrip = false)
    {
        var state = context.State;
        if (!longTrip && context.Source is { Items.Length: > 0 } || context.AlternativeScopes.Count > 0 || state.FocusDay.HasValue ||
            context.Request.ConfirmedState?.FocusDay is not null || context.Request.SelectedItemKey is not null) return null;
        var meals = state.MealSlots.Length > 0 ? AgentStateValidator.Meals.Where(state.MealSlots.Contains).ToArray() : ["breakfast", "lunch", "dinner"];
        AgentCandidateGrant[] grants = longTrip ? [.. context.Grants, .. context.ReserveGrants] : [.. context.Grants];
        var used = new Dictionary<int, int>();
        var items = new List<AgentItem>();
        for (short day = 1; day <= (state.NumDays ?? 0); day++)
            foreach (var meal in meals)
            {
                if (longTrip && state.SkippedMeals?.Contains(new(day, meal)) == true) continue;
                var fingerprint = AgentScopedPlanning.Fingerprint(state, day, meal);
                var options = grants.Where(grant => (!grant.DayNumber.HasValue || grant.DayNumber == day) && (grant.MealSlot is null || grant.MealSlot == meal) &&
                        AgentScopedPlanning.Fingerprint(grant.Constraints, day, meal) == fingerprint)
                    .SelectMany(grant => grant.DishIds).ToArray();
                var pick = options.Where(id => !used.ContainsKey(id)).Select(id => (int?)id).FirstOrDefault();
                if (pick is null && longTrip && options.Length > 0) pick = options.MinBy(id => used[id]);
                if (pick is not { } dishId) return null;
                used[dishId] = used.GetValueOrDefault(dishId) + 1;
                items.Add(new($"day{day}-{meal}", null, day, meal, 0, dishId));
            }
        return items.Count == 0 ? null : new AgentOutput { Action = "create_proposal", Reply = "", Items = items.ToArray() };
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
        item.DishId, source?.DishId == item.DishId ? source.RequestId : null,
        RankingFingerprint: source?.DishId == item.DishId ? source.RankingFingerprint : null);

    /// <summary>Creates the documented deterministic rejection without changing any persisted proposal.</summary>
    private static ConflictException Invalid(string message) => new("PROPOSAL_NOT_COMMITTABLE", message);
}
