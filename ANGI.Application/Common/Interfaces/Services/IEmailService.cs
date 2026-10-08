namespace ANGI.Application.Common.Interfaces.Services
{
    /// <summary>
    /// Sends the account emails. Every method throws <c>ServiceUnavailableException</c>
    /// (<c>SERVICE_UNAVAILABLE</c>) when the email cannot be sent.
    /// </summary>
    public interface IEmailService
    {
        /// <summary>
        /// Sends the link <c>{Frontend:BaseUrl}/verify-email?token=...</c> (AUTH-01, AUTH-03).
        /// </summary>
        /// <param name="token">The raw token; only its hash is stored in <c>user_tokens</c>.</param>
        /// <param name="validFor">How long the token is valid, shown in the email.</param>
        Task SendEmailVerificationAsync(
            string toEmail,
            string displayName,
            string token,
            TimeSpan validFor,
            CancellationToken ct);

        /// <summary>
        /// Sends the link <c>{Frontend:BaseUrl}/reset-password?token=...</c> (AUTH-08).
        /// </summary>
        /// <param name="token">The raw token; only its hash is stored in <c>user_tokens</c>.</param>
        /// <param name="validFor">How long the token is valid, shown in the email.</param>
        Task SendPasswordResetAsync(
            string toEmail,
            string displayName,
            string token,
            TimeSpan validFor,
            CancellationToken ct);
    }
}
