namespace ANGI.Application.Common.Interfaces.Services.Recommendation
{
    public interface IRecommendationService
    {
        Task<bool?> GetSurveyCompletionAsync(int userId, CancellationToken ct);
    }
}
