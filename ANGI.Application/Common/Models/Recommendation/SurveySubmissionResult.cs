namespace ANGI.Application.Common.Models.Recommendation
{
    /// <summary>What RECO-03 stored for the user.</summary>
    public sealed record SurveySubmissionResult(int AnswerCount, bool HasVector, DateTime SurveyAt);
}
