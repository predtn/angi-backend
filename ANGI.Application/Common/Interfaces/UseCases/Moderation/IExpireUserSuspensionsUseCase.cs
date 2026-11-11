namespace ANGI.Application.Common.Interfaces.UseCases.Moderation;

/// <summary>Restores accounts whose temporary suspension deadline has passed.</summary>
public interface IExpireUserSuspensionsUseCase
{
    /// <summary>Atomically expires one bounded batch and returns the number of restored accounts.</summary>
    Task<int> ExecuteAsync(CancellationToken ct);
}
