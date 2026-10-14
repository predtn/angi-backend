namespace ANGI.Application.DTOs.Discovery;

/// <summary>Response data of PREF-03.</summary>
public sealed class SkipPreferenceSurveyResponseDto
{
    /// <summary>Always true.</summary>
    public bool Completed { get; set; }

    /// <summary>Always true: reco stored an empty answer list.</summary>
    public bool Skipped { get; set; }
}
