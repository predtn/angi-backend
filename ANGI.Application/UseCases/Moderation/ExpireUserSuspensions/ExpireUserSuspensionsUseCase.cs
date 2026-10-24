using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Moderation;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Moderation;
using ANGI.Domain.Enums;

namespace ANGI.Application.UseCases.Moderation.ExpireUserSuspensions;

/// <summary>Restores temporarily suspended accounts after their documented suspension deadline.</summary>
public sealed class ExpireUserSuspensionsUseCase(IRestaurantModerationRepository repository, IUnitOfWork unitOfWork,
    IAccountStatusCache cache, TimeProvider clock) : IExpireUserSuspensionsUseCase
{
    /// <summary>Locks expired suspensions against concurrent sanctions, saves once, and invalidates caches after commit.</summary>
    public async Task<int> ExecuteAsync(CancellationToken ct)
    {
        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);
        var users = await repository.LockExpiredSuspensionsAsync(clock.GetUtcNow().UtcDateTime, ct);
        foreach (var user in users)
        {
            user.Status = UserStatus.Active;
            user.SuspendedUntil = null;
        }
        if (users.Count > 0) await unitOfWork.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        foreach (var user in users) cache.Invalidate(user.Id);
        return users.Count;
    }
}
