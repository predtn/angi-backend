using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Discovery;
using ANGI.Application.Common.Interfaces.Repositories.Roadmap;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Interfaces.Services.Recommendation;
using ANGI.Application.Common.Interfaces.UseCases.Roadmap;
using ANGI.Application.Common.Models.Discovery;
using ANGI.Application.Common.Models.Recommendation;
using ANGI.Application.Common.Models.Roadmap;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;

namespace ANGI.Application.UseCases.Roadmap.Generate;

/// <summary>
/// Runs one RM-10 job: (1) the origin and radius stored when it was queued, (2) candidates filtered by the constraints
/// and the province, inside the radius when there is an origin (the whole province when no restaurant is inside),
/// (3) reco ranking with context = roadmap_ai, (4) one geographic cluster per day, (5) the AI schedules each day from its
/// cluster (the job's own schedule when the AI is unavailable or unusable), (6) the items are written. A succeeded job
/// notifies the traveler (ROADMAP_GENERATED) and reports the dishes it added to reco as shown (RECO-06).
/// </summary>
public sealed class ProcessRoadmapGenerationJobUseCase : IProcessRoadmapGenerationJobUseCase
{
    public const int MaxCandidates = 2000;
    public const int MaxRanked = 200;

    /// <summary>roadmap_generation_jobs.model_name and a day's plannedBy when the job built the schedule itself.</summary>
    public const string FallbackPlanner = "fallback";

    public const string NotificationType = "ROADMAP_GENERATED";
    public const string NoCandidateMessage = "Không tìm thấy món phù hợp với các điều kiện đã chọn.";
    public const string RoadmapGoneMessage = "Roadmap đã bị xóa trước khi tạo xong.";
    public const string DayGoneMessage = "Ngày được chọn không còn trong roadmap.";
    public const string UnexpectedMessage = "Không tạo được roadmap, vui lòng thử lại.";

    private readonly IRoadmapGenerationRepository _generationRepository;
    private readonly IRoadmapRepository _roadmapRepository;
    private readonly IDishRepository _dishRepository;
    private readonly IRecommendationService _recommendationService;
    private readonly IRoadmapPlannerService _plannerService;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes persistence, reco, AI, transaction and clock dependencies.</summary>
    public ProcessRoadmapGenerationJobUseCase(
        IRoadmapGenerationRepository generationRepository,
        IRoadmapRepository roadmapRepository,
        IDishRepository dishRepository,
        IRecommendationService recommendationService,
        IRoadmapPlannerService plannerService,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider)
    {
        _generationRepository = generationRepository;
        _roadmapRepository = roadmapRepository;
        _dishRepository = dishRepository;
        _recommendationService = recommendationService;
        _plannerService = plannerService;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(long jobId, CancellationToken ct)
    {
        try
        {
            await RunAsync(jobId, ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // A job never stays running after an error; the worker logs the exception.
            await _generationRepository.MarkFailedAsync(jobId, UnexpectedMessage, CancellationToken.None);
            throw;
        }
    }

    private async Task RunAsync(long jobId, CancellationToken ct)
    {
        var job = await _generationRepository.GetJobSnapshotAsync(jobId, ct);
        if (job is null)
        {
            return;
        }

        var roadmap = job.Roadmap;
        if (roadmap.DeletedAt is not null)
        {
            await _generationRepository.MarkFailedAsync(jobId, RoadmapGoneMessage, ct);
            return;
        }

        var input = RoadmapGenerationValues.ReadInput(job.Input);
        var request = input.Request;
        var days = job.Scope == GenerationScope.Full
            ? roadmap.Days.OrderBy(day => day.DayNumber).ToArray()
            : roadmap.Days.Where(day => day.Id == request.DayId).ToArray();
        if (days.Length == 0)
        {
            await _generationRepository.MarkFailedAsync(jobId, DayGoneMessage, ct);
            return;
        }

        IReadOnlyList<MealSlot> meals = job.Scope == GenerationScope.Slot
            ? [RoadmapGenerationValues.ParseMealSlot(request.MealSlot!)]
            : request.MealSlots?.Select(RoadmapGenerationValues.ParseMealSlot).OrderBy(meal => meal).ToArray()
              ?? RoadmapGenerationValues.DefaultMealSlots;
        var replace = request.ReplaceExisting == true;
        var targets = days
            .Select(day => (Day: day, Meals: replace
                ? meals
                : (IReadOnlyList<MealSlot>)meals.Where(meal => day.Items.All(item => item.MealSlot != meal)).ToArray()))
            .Where(target => target.Meals.Count > 0)
            .ToArray();

        // Dishes that stay in the roadmap are not suggested again.
        var targetDayIds = days.Select(day => day.Id).ToHashSet();
        var keptDishIds = roadmap.Days
            .SelectMany(day => day.Items.Where(item => !(replace && targetDayIds.Contains(day.Id) && meals.Contains(item.MealSlot))))
            .Select(item => item.DishId)
            .Distinct()
            .ToArray();

        IReadOnlyList<int> candidates = [];
        var radiusDropped = false;
        RecommendationResult? ranked = null;
        var plannedDays = new List<(RoadmapDay Day, string PlannedBy, IReadOnlyList<RoadmapPlanAssignment> Assignments)>();
        string? modelName = null;
        if (targets.Length > 0)
        {
            var area = input.Origin is { } origin && input.RadiusKm is { } radius
                ? new NearbyArea(origin.Latitude, origin.Longitude, radius)
                : null;
            var filter = new DishCandidateFilter
            {
                Province = request.Province,
                ExcludeTagIds = request.ExcludeTagIds ?? [],
                MaxPrice = request.MaxPricePerMeal,
                ExcludeDishIds = keptDishIds,
                Area = area
            };
            candidates = await _dishRepository.GetCandidateIdsAsync(filter, MaxCandidates, ct);
            if (candidates.Count == 0 && area is not null)
            {
                // No restaurant around the origin (e.g. GPS in another province): search the province like origin none.
                candidates = await _dishRepository.GetCandidateIdsAsync(filter with { Area = null }, MaxCandidates, ct);
                radiusDropped = true;
            }

            if (candidates.Count == 0)
            {
                await _generationRepository.MarkFailedAsync(jobId, NoCandidateMessage, ct);
                return;
            }

            ranked = await _recommendationService.RecommendAsync(
                job.RequestedBy, candidates, MaxRanked, RecommendationContext.RoadmapAi, ct);
            var candidateSet = candidates.ToHashSet();
            var rankedIds = ranked.DishIds.Where(candidateSet.Contains).Distinct().ToArray();
            var dishes = (await _dishRepository.GetCardDishesAsync(rankedIds, ct)).ToDictionary(dish => dish.Id);
            var ordered = rankedIds.Where(dishes.ContainsKey).Select(id => dishes[id]).ToList();
            var preferredTagIds = (request.TagIds ?? []).ToHashSet();
            var preferredTags = ordered
                .SelectMany(dish => dish.DishTags)
                .Where(dishTag => preferredTagIds.Contains(dishTag.TagId))
                .Select(dishTag => dishTag.Tag.Name)
                .Distinct()
                .ToArray();

            var used = new HashSet<int>();
            foreach (var (day, dayMeals) in targets)
            {
                var available = ordered.Where(dish => !used.Contains(dish.Id)).ToArray();
                if (available.Length == 0)
                {
                    break;
                }

                var cluster = RoadmapDayPlanner.Cluster(available, dayMeals.Count);
                var anchor = cluster[0];
                var plan = await _plannerService.PlanDayAsync(new RoadmapPlanRequest(
                    day.DayNumber,
                    dayMeals,
                    cluster.Select(dish => new RoadmapPlanCandidate(
                        dish.Id,
                        dish.Name,
                        dish.Restaurant.Name,
                        dish.Price,
                        dish.DishTags.Select(dishTag => dishTag.Tag.Name).ToArray(),
                        Math.Round(RoadmapDayPlanner.DistanceKm(anchor, dish), 2))).ToArray(),
                    preferredTags,
                    request.Note), ct);
                var accepted = RoadmapDayPlanner.Accept(plan, dayMeals, cluster.Select(dish => dish.Id).ToArray());
                if (accepted is not null)
                {
                    modelName = plan!.ModelName;
                }

                var assignments = accepted ?? RoadmapDayPlanner.Fallback(cluster, dayMeals, preferredTagIds);
                used.UnionWith(assignments.Select(assignment => assignment.DishId));
                plannedDays.Add((day, accepted is null ? FallbackPlanner : plan!.ModelName, assignments));
            }
        }

        await WriteAsync(job, input with
        {
            RadiusKm = radiusDropped ? null : input.RadiusKm,
            RadiusDropped = radiusDropped,
            CandidateDishIds = candidates
        }, replace, targetDayIds, meals, ranked?.RequestId, modelName, plannedDays, ct);
    }

    /// <summary>Step 6 in one transaction holding the roadmap lock: replace or add the items, finish the job, notify.</summary>
    private async Task WriteAsync(
        RoadmapGenerationJob job,
        RoadmapGenerationInput input,
        bool replace,
        IReadOnlySet<long> targetDayIds,
        IReadOnlyList<MealSlot> meals,
        Guid? recoRequestId,
        string? modelName,
        IReadOnlyList<(RoadmapDay Day, string PlannedBy, IReadOnlyList<RoadmapPlanAssignment> Assignments)> plannedDays,
        CancellationToken ct)
    {
        await using var transaction = await _unitOfWork.BeginTransactionAsync(ct);
        var roadmap = await _roadmapRepository.LockAndGetTrackedAsync(job.RoadmapId, job.Roadmap.UserId, ct);
        if (roadmap is null)
        {
            await _generationRepository.MarkFailedAsync(job.Id, RoadmapGoneMessage, ct);
            await transaction.CommitAsync(ct);
            return;
        }

        var removed = new HashSet<long>();
        if (replace)
        {
            foreach (var item in roadmap.Days
                         .Where(day => targetDayIds.Contains(day.Id))
                         .SelectMany(day => day.Items)
                         .Where(item => meals.Contains(item.MealSlot))
                         .ToArray())
            {
                _roadmapRepository.RemoveItem(item);
                removed.Add(item.Id);
            }
        }

        var created = new List<RoadmapItem>();
        foreach (var (plannedDay, _, assignments) in plannedDays)
        {
            var day = roadmap.Days.FirstOrDefault(value => value.Id == plannedDay.Id);
            if (day is null)
            {
                continue;
            }

            foreach (var assignment in assignments)
            {
                var sortOrders = day.Items
                    .Where(item => item.MealSlot == assignment.MealSlot && !removed.Contains(item.Id))
                    .Select(item => (int)item.SortOrder)
                    .Concat(created.Where(item => item.RoadmapDayId == day.Id && item.MealSlot == assignment.MealSlot)
                        .Select(item => (int)item.SortOrder))
                    .ToArray();
                var item = new RoadmapItem
                {
                    RoadmapDayId = day.Id,
                    DishId = assignment.DishId,
                    MealSlot = assignment.MealSlot,
                    SortOrder = (short)(sortOrders.Length == 0 ? 0 : sortOrders.Max() + 1),
                    Source = RoadmapItemSource.Ai,
                    RecoRequestId = recoRequestId
                };
                _roadmapRepository.AddItem(item);
                created.Add(item);
            }
        }

        await _unitOfWork.SaveChangesAsync(ct);

        var tracked = await _generationRepository.GetTrackedJobAsync(job.Id, ct)
            ?? throw new InvalidOperationException($"Generation job {job.Id} disappeared while running.");
        tracked.Input = RoadmapGenerationValues.Serialize(input);
        tracked.RecoRequestId = recoRequestId;
        tracked.ModelName = modelName ?? FallbackPlanner;
        tracked.PromptVersion = _plannerService.PromptVersion;
        tracked.RawOutput = RoadmapGenerationValues.Serialize(new RoadmapGenerationOutput(
            plannedDays.Select(planned => new RoadmapGenerationDay(
                planned.Day.Id,
                planned.Day.DayNumber,
                planned.PlannedBy,
                planned.Assignments
                    .Select(assignment => new RoadmapGenerationAssignment(
                        RoadmapGenerationValues.ToApi(assignment.MealSlot), assignment.DishId))
                    .ToArray()))
                .ToArray(),
            created.Select(item => item.Id).ToArray()));
        tracked.Status = GenerationJobStatus.Succeeded;
        tracked.ErrorMessage = null;
        tracked.FinishedAt = _timeProvider.GetUtcNow().UtcDateTime;
        _generationRepository.AddNotification(new Domain.Entities.Notification
        {
            RecipientId = job.RequestedBy,
            Type = NotificationType,
            Title = "Roadmap đã được tạo xong",
            Body = $"AI đã thêm {created.Count} món vào roadmap \"{roadmap.Title}\".",
            Data = RoadmapGenerationValues.Serialize(new { roadmapId = roadmap.Id, jobId = job.Id })
        });
        await _unitOfWork.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        if (recoRequestId is { } requestId && created.Count > 0)
        {
            // RM-10: the backend reports the itinerary's dishes as shown; RECO-06 never throws.
            await _recommendationService.RecordImpressionsAsync(
                job.RequestedBy,
                requestId,
                created.Select(item => item.DishId).Distinct().ToArray(),
                _timeProvider.GetUtcNow().UtcDateTime,
                ct);
        }
    }
}
