namespace ANGI.Application.DTOs.Discovery;

/// <summary>Request body of PREF-02: the survey answers forwarded to reco (RECO-03).</summary>
public sealed class SubmitPreferenceSurveyRequestDto
{
    /// <summary>1..50 answers; the questions are hardcoded in the frontend.</summary>
    public List<SurveyAnswerDto> Answers { get; set; } = [];
}
