using ANGI.Domain.Entities;

namespace ANGI.Application.Common.Interfaces.Repositories.Media;

/// <summary>Defines persistence operations used by media upload and media ownership checks.</summary>
public interface IMediaRepository
{
    /// <summary>Loads media owned by a user for the requested identifiers.</summary>
    Task<IReadOnlyList<MediaFile>> GetOwnedByIdsAsync(
        int uploaderId,
        IReadOnlyCollection<long> mediaIds,
        CancellationToken ct);

    /// <summary>Adds a media file to the current unit of work.</summary>
    void Add(MediaFile mediaFile);
}
