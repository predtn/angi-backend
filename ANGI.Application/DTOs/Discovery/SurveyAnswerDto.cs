namespace ANGI.Application.DTOs.Discovery;

/// <summary>One PREF-02 answer.</summary>
public sealed class SurveyAnswerDto
{
    /// <summary>
    /// Must exist in recommendation.survey_option_vectors.option_code (checked by reco). Nullable for the same reason
    /// as <see cref="SubmitPreferenceSurveyRequestDto.Answers"/>.
    /// </summary>
    public string? OptionCode { get; set; }

    /// <summary>0..1 for scale questions; null counts as 1.</summary>
    public decimal? ScaleValue { get; set; }
}
