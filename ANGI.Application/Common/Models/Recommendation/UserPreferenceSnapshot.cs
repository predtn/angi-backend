namespace ANGI.Application.Common.Models.Recommendation;

/// <summary>A successful read, including an empty profile; null result means unavailable.</summary>
public sealed record UserPreferenceSnapshot(int UserId, int OntologyVersion, double[]? Vec, DateTime? UpdatedAt);

/// <summary>Explicitly distinguishes an absent conversation vector from profile ranking.</summary>
public sealed record RecommendationVector(string VectorSource, double[]? UserVectorOverride);
