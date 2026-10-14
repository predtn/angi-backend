using ANGI.Application.Common.Interfaces.Repositories.Discovery;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ANGI.Infrastructure.Persistences.Repositories.Discovery;

/// <summary>Reads the tag catalogue.</summary>
public sealed class TagRepository : ITagRepository
{
    private readonly ANGIContext _context;

    public TagRepository(ANGIContext context)
    {
        _context = context;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Tag>> GetTagsAsync(TagType? type, CancellationToken ct)
    {
        var query = _context.Tags.AsNoTracking();
        if (type is { } tagType)
        {
            query = query.Where(tag => tag.TagType == tagType);
        }

        return await query.ToListAsync(ct);
    }
}
