using ANGI.Application.Common.Interfaces.Repositories.Media;
using ANGI.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ANGI.Infrastructure.Persistences.Repositories.Media;

/// <summary>Provides EF Core persistence operations for media records.</summary>
public sealed class MediaRepository : IMediaRepository
{
    private readonly ANGIContext _context;

    /// <summary>Initializes the repository with the scoped application context.</summary>
    public MediaRepository(ANGIContext context)
    {
        _context = context;
    }

    /// <summary>Loads only media records owned by the specified uploader.</summary>
    public async Task<IReadOnlyList<MediaFile>> GetOwnedByIdsAsync(
        int uploaderId,
        IReadOnlyCollection<long> mediaIds,
        CancellationToken ct)
    {
        return await _context.MediaFiles
            .AsNoTracking()
            .Where(media =>
                media.UploaderId == uploaderId &&
                mediaIds.Contains(media.Id))
            .ToListAsync(ct);
    }

    /// <summary>Adds a media record to the context without saving immediately.</summary>
    public void Add(MediaFile mediaFile)
    {
        _context.MediaFiles.Add(mediaFile);
    }
}
