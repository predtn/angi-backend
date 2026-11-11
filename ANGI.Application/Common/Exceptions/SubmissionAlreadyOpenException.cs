namespace ANGI.Application.Common.Exceptions;

/// <summary>Represents SUBMISSION_ALREADY_OPEN together with the existing submission id.</summary>
public sealed class SubmissionAlreadyOpenException : ConflictException
{
    /// <summary>Creates the documented conflict and retains the open submission identifier.</summary>
    public SubmissionAlreadyOpenException(long submissionId)
        : base("SUBMISSION_ALREADY_OPEN", "Nhà hàng đã có một bản menu đang mở.")
    {
        SubmissionId = submissionId;
    }

    public long SubmissionId { get; }
}
