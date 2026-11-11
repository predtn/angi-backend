namespace ANGI.Application.Common.Interfaces.UseCases.Auth
{
    /// <summary>Defines the check that blocks an account which is not active on endpoints that need a token.</summary>
    public interface IEnsureActiveAccountUseCase
    {
        /// <summary>Returns when the account is active; otherwise throws the AUTH-04 error for its status.</summary>
        Task ExecuteAsync(int userId, CancellationToken ct);
    }
}
