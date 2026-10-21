using ANGI.Application.Common.Interfaces.Repositories.Roadmap;
using ANGI.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ANGI.Infrastructure.Persistences.Repositories.Roadmap;

/// <summary>Implements roadmap queries for snapshot generation and use cases.</summary>
public sealed class RoadmapRepository : IRoadmapRepository
{
    private readonly ANGIContext _context;

    public RoadmapRepository(ANGIContext context)
    {
        _context = context;
    }

    /// <inheritdoc />
    public async Task<Domain.Entities.Roadmap?> GetByIdForSnapshotAsync(long id, int userId, CancellationToken ct)
    {
        return await _context.Roadmaps
            .AsNoTracking()
            .Where(r => r.Id == id && r.UserId == userId)
            .Include(r => r.Days)
                .ThenInclude(d => d.Items)
                    .ThenInclude(i => i.Dish)
                        .ThenInclude(dish => dish.Restaurant)
            .AsSplitQuery()
            .FirstOrDefaultAsync(ct);
    }
}
