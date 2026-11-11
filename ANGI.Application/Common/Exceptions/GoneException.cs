namespace ANGI.Application.Common.Exceptions;

/// <summary>Signals the documented expiry of short-term Agent resources.</summary>
public sealed class GoneException : AppException
{
    /// <summary>Creates an expiry error with a stable code and user-facing explanation.</summary>
    public GoneException(string errorCode, string message) : base(errorCode, message) { }
}
