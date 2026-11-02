using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Moderation;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Moderation;
using ANGI.Application.DTOs.Account;
using ANGI.Application.DTOs.Moderation;
using ANGI.Domain.Enums;
using FluentValidation;

namespace ANGI.Application.UseCases.Moderation.CreateUserSanction;

/// <summary>Implements MOD-U3: validates, authorizes and executes user sanctions.</summary>
public sealed class CreateUserSanctionUseCase(
    IValidator<CreateUserSanctionRequestDto> validator,
    ModerationAccess access,
    IUserModerationRepository repository,
    IUnitOfWork unitOfWork,
    UserSanctionEffects effects,
    IAccountStatusCache accountStatusCache,
    TimeProvider clock) : ICreateUserSanctionUseCase
{
    /// <summary>Executes the sanction workflow within a database transaction.</summary>
    public async Task<SanctionDto> ExecuteAsync(int userId, CreateUserSanctionRequestDto request, CancellationToken ct)
    {
        await validator.ValidateAndThrowAsync(request, ct);

        var sanctionType = ParseSanctionType(request.Type);
        var actorId = await access.RequireAsync(ct, sanctionType == SanctionType.Ban ? "USER_BAN" : "USER_SUSPEND");

        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);

        var user = await repository.LockUserAsync(userId, ct)
            ?? throw new NotFoundException("USER_NOT_FOUND", "Không tìm thấy người dùng.");

        var now = clock.GetUtcNow().UtcDateTime;
        DateTime? endsAt = null;
        if (sanctionType == SanctionType.Suspend)
        {
            if (request.EndsAt.HasValue)
            {
                endsAt = request.EndsAt.Value.UtcDateTime;
            }
            else if (request.DurationDays.HasValue)
            {
                endsAt = clock.GetUtcNow().AddDays(request.DurationDays.Value).UtcDateTime;
            }
        }

        var sanction = await effects.ApplyAsync(
            repository, user, sanctionType, request.Reason.Trim(), endsAt, request.ReportId, actorId, now, ct);

        await unitOfWork.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        if (sanctionType != SanctionType.Warn)
        {
            accountStatusCache.Invalidate(userId);
        }

        var issuer = await repository.GetUserSummaryAsync(actorId, ct)
            ?? new UserSummaryDto { Id = actorId };

        return ModerationMapper.Sanction(sanction, issuer);
    }

    private static SanctionType ParseSanctionType(string type) =>
        type.Trim().ToLowerInvariant() switch
        {
            "warn" => SanctionType.Warn,
            "suspend" => SanctionType.Suspend,
            "ban" => SanctionType.Ban,
            _ => throw new BadRequestException("VALIDATION_FAILED", "Hình thức xử phạt không hợp lệ.")
        };
}
