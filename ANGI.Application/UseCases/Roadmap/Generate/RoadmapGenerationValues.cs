using System.Text.Json;
using ANGI.Application.Common.Models.Roadmap;
using ANGI.Application.UseCases.Validators.Roadmap;
using ANGI.Domain.Enums;

namespace ANGI.Application.UseCases.Roadmap.Generate;

/// <summary>API values and JSON of RM-10 / RM-11 jobs.</summary>
internal static class RoadmapGenerationValues
{
    public const string FullScope = GenerateRoadmapRequestDtoValidator.ScopeFull;

    /// <summary>Meals generated for scope full / day when the request leaves mealSlots out.</summary>
    public static readonly IReadOnlyList<MealSlot> DefaultMealSlots = [MealSlot.Breakfast, MealSlot.Lunch, MealSlot.Dinner];

    /// <summary>camelCase JSON for roadmap_generation_jobs.input and raw_output.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string ToApi<TEnum>(TEnum value) where TEnum : struct, Enum =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());

    public static GenerationScope ParseScope(string value) => value switch
    {
        GenerateRoadmapRequestDtoValidator.ScopeDay => GenerationScope.Day,
        GenerateRoadmapRequestDtoValidator.ScopeSlot => GenerationScope.Slot,
        _ => GenerationScope.Full
    };

    public static MealSlot ParseMealSlot(string value) => value switch
    {
        "breakfast" => MealSlot.Breakfast,
        "lunch" => MealSlot.Lunch,
        "dinner" => MealSlot.Dinner,
        "snack" => MealSlot.Snack,
        "late_night" => MealSlot.LateNight,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown meal slot.")
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);

    public static RoadmapGenerationInput ReadInput(string json) =>
        JsonSerializer.Deserialize<RoadmapGenerationInput>(json, Json)
        ?? throw new InvalidOperationException("roadmap_generation_jobs.input is empty.");

    /// <summary>CreatedItemIds of raw_output, or none while the job has no output.</summary>
    public static IReadOnlyList<long> ReadCreatedItemIds(string? rawOutput) =>
        string.IsNullOrEmpty(rawOutput)
            ? []
            : JsonSerializer.Deserialize<RoadmapGenerationOutput>(rawOutput, Json)?.CreatedItemIds ?? [];
}
