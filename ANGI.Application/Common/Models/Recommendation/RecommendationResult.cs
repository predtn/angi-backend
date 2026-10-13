namespace ANGI.Application.Common.Models.Recommendation
{
    /// <summary>
    /// RECO-05 result. <see cref="RequestId"/> is null for a Guest and for the fallback, which returns
    /// the candidates in the order sent.
    /// </summary>
    public sealed record RecommendationResult(
        Guid? RequestId,
        IReadOnlyList<int> DishIds,
        IReadOnlyList<int> IgnoredDishIds);
}
