namespace ANGI.Application.Common.Exceptions
{
    /// <summary>Represents a suspended-account error together with the suspension end time.</summary>
    public sealed class AccountSuspendedException : ForbiddenException
    {
        /// <summary>Creates ACCOUNT_SUSPENDED and retains suspendedUntil for a structured WebApi response.</summary>
        public AccountSuspendedException(DateTime? suspendedUntil)
            : base("ACCOUNT_SUSPENDED", "Tài khoản đang bị tạm khóa.")
        {
            SuspendedUntil = suspendedUntil;
        }

        public DateTime? SuspendedUntil { get; }
    }
}
