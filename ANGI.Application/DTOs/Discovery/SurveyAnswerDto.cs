namespace ANGI.Application.DTOs.Discovery;

/// <summary>One PREF-02 answer.</summary>
public sealed class SurveyAnswerDto
{
    /// <summary>Must exist in recommendation.survey_option_vectors.option_code (checked by reco).</summary>
    public string OptionCode { get; set; } = string.Empty;

    /// <summary>0..1 for scale questions; null counts as 1.</summary>
    public decimal? ScaleValue { get; set; }
}
