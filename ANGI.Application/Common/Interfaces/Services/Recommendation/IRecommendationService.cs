using ANGI.Application.Common.Models.Recommendation;

namespace ANGI.Application.Common.Interfaces.Services.Recommendation
{
    /// <summary>
    /// Client of the recommendation service (sheet "Reco API (nội bộ)"). Each method already turns reco
    /// failures into the outcome the API Design gives (project_architecture.md §8).
    /// </summary>
    public interface IRecommendationService
    {
        /// <summary>Returns a successful profile snapshot; null means the service is unavailable.</summary>
        Task<UserPreferenceSnapshot?> GetPreferencesAsync(int userId, CancellationToken ct) => Task.FromResult<UserPreferenceSnapshot?>(null);

        /// <summary>Ranks a conversation using its request vector; implementations must confirm support.</summary>
        Task<RecommendationResult> RecommendConversationAsync(int userId, IReadOnlyList<int> candidates, int topK,
            RecommendationVector vector, CancellationToken ct) => RecommendAsync(userId, candidates, topK, RecommendationContext.RoadmapAi, ct);
        /// <summary>
        /// RECO-03 (PREF-02, PREF-03 with no answers). Throws 409 SURVEY_ALREADY_SUBMITTED, 400 UNKNOWN_OPTION_CODE,
        /// or 503 SERVICE_UNAVAILABLE when reco fails.
        /// </summary>
        Task<SurveySubmissionResult> SubmitSurveyAsync(
            int userId,
            IReadOnlyList<SurveyAnswer> answers,
            DateTime? occurredAt,
            CancellationToken ct);

        /// <summary>
        /// RECO-05. <paramref name="candidateDishIds"/> must already be sorted by restaurant rating, then like_count;
        /// only the first 2000 are sent. When reco fails, returns the candidates in that order with requestId = null.
        /// </summary>
        /// <param name="userId">Null for a Guest.</param>
        /// <param name="topK">1..200; null uses reco's default of 50.</param>
        Task<RecommendationResult> RecommendAsync(
            int? userId,
            IReadOnlyList<int> candidateDishIds,
            int? topK,
            RecommendationContext context,
            CancellationToken ct);

        /// <summary>RECO-06 (IMP-01, DISH-03, RM-10). Never throws for a reco failure; it is only logged.</summary>
        Task RecordImpressionsAsync(
            int userId,
            Guid requestId,
            IReadOnlyList<int> dishIds,
            DateTime? shownAt,
            CancellationToken ct);

        /// <summary>RECO-09 (AUTH-04, AUTH-05, AUTH-06, ME-01). Null when reco cannot answer.</summary>
        Task<SurveyStatus?> GetSurveyStatusAsync(int userId, CancellationToken ct);
    }
}
