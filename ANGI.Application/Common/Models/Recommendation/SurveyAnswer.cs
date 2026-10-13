namespace ANGI.Application.Common.Models.Recommendation
{
    /// <summary>One survey answer sent to RECO-03; a missing scale value counts as 1.</summary>
    public sealed record SurveyAnswer(string OptionCode, decimal? ScaleValue = null);
}
