namespace ANGI.Application.Common.Models.Recommendation
{
    /// <summary>
    /// RECO-09. <see cref="Completed"/> is true when the user answered or skipped the survey;
    /// a skipped survey is completed with <see cref="AnswerCount"/> = 0.
    /// </summary>
    public sealed record SurveyStatus(bool Completed, DateTime? SurveyAt, int AnswerCount);
}
