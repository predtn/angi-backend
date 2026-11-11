using ANGI.Application.DTOs.Roadmap;
using ANGI.Domain.Enums;

namespace ANGI.Application.Common.Models.Roadmap;

/// <summary>Origin of an RM-10 job: the GPS sent with the request (gps roadmap) or the pinned point.</summary>
public sealed record RoadmapGenerationOrigin(string Mode, decimal Latitude, decimal Longitude);

/// <summary>
/// roadmap_generation_jobs.input: the RM-10 constraints, the origin and radius used, and the candidates sent to the AI
/// (written by the job). RadiusKm is null without an origin or when no restaurant was inside it.
/// </summary>
public sealed record RoadmapGenerationInput
{
    public GenerateRoadmapRequestDto Request { get; init; } = null!;
    public RoadmapGenerationOrigin? Origin { get; init; }
    public decimal? RadiusKm { get; init; }

    /// <summary>True when the origin had no candidate within RadiusKm, so the job searched the whole province.</summary>
    public bool RadiusDropped { get; init; }

    public IReadOnlyList<int> CandidateDishIds { get; init; } = [];
}

/// <summary>One meal of one day filled by the job.</summary>
public sealed record RoadmapGenerationAssignment(string MealSlot, int DishId);

/// <summary>What the job decided for one day: who planned it (the AI model or the fallback) and the meals.</summary>
public sealed record RoadmapGenerationDay(
    long DayId,
    short DayNumber,
    string PlannedBy,
    IReadOnlyList<RoadmapGenerationAssignment> Assignments);

/// <summary>roadmap_generation_jobs.raw_output; RM-11 reads CreatedItemIds from it.</summary>
public sealed record RoadmapGenerationOutput(
    IReadOnlyList<RoadmapGenerationDay> Days,
    IReadOnlyList<long> CreatedItemIds);

/// <summary>A candidate dish of one day's cluster, in reco rank order.</summary>
public sealed record RoadmapPlanCandidate(
    int DishId,
    string DishName,
    string RestaurantName,
    decimal Price,
    IReadOnlyList<string> Tags,
    double DistanceFromAnchorKm);

/// <summary>What the AI planner receives to schedule one day (RM-10 step 5).</summary>
public sealed record RoadmapPlanRequest(
    short DayNumber,
    IReadOnlyList<MealSlot> MealSlots,
    IReadOnlyList<RoadmapPlanCandidate> Candidates,
    IReadOnlyList<string> PreferredTags,
    string? Note);

/// <summary>The AI planner's choice of one dish for one meal.</summary>
public sealed record RoadmapPlanAssignment(MealSlot MealSlot, int DishId);

/// <summary>The AI planner's schedule for one day and the model that produced it.</summary>
public sealed record RoadmapPlanResult(string ModelName, IReadOnlyList<RoadmapPlanAssignment> Assignments);
