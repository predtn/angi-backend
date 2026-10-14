using ANGI.Application.Common.Interfaces.Repositories.Account;
using ANGI.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ANGI.Infrastructure.Persistences.Repositories.Account;

/// <summary>Provides the EF Core implementation of Account persistence operations.</summary>
public sealed class AccountRepository : IAccountRepository
{
    private readonly ANGIContext _context;

    public AccountRepository(ANGIContext context)
    {
        _context = context;
    }

    public Task<User?> GetUserByIdAsync(int userId, CancellationToken ct)
    {
        return _context.Users.FirstOrDefaultAsync(user => user.Id == userId, ct);
    }

    public async Task<IReadOnlyList<UserSession>> GetActiveSessionsAsync(int userId, CancellationToken ct)
    {
        return await _context.UserSessions
            .Where(session => session.UserId == userId && session.RevokedAt == null)
            .ToListAsync(ct);
    }
}
