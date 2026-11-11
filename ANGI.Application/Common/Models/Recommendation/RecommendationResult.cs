namespace ANGI.Application.Common.Models.Recommendation
{
    /// <summary>
    /// RECO-05 result. <see cref="RequestId"/> is null for a Guest and for the fallback, which returns
    /// the candidates in the order sent. <see cref="Scores"/> holds reco's recommend_score of each dish,
    /// in the order of <see cref="DishIds"/>; null for the fallback, which ranks nothing.
    /// </summary>
    public sealed record RecommendationResult(
        Guid? RequestId,
        IReadOnlyList<int> DishIds,
        IReadOnlyList<int> IgnoredDishIds,
        IReadOnlyList<double>? Scores = null,
        string? VectorSource = null);
}
