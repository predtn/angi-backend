namespace ANGI.Application.DTOs.Discovery;

/// <summary>Response data of PREF-02.</summary>
public sealed class SubmitPreferenceSurveyResponseDto
{
    /// <summary>Always true.</summary>
    public bool Completed { get; set; }

    /// <summary>survey_answer_count returned by reco.</summary>
    public int AnswerCount { get; set; }

    /// <summary>Whether reco could build the initial preference vector.</summary>
    public bool HasVector { get; set; }
}
