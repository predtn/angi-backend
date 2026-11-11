using ANGI.Domain.Entities;
using ANGI.Domain.Enums;

namespace ANGI.Application.Common.Interfaces.Repositories.Discovery;

/// <summary>Reads the tag catalogue (CAT-02).</summary>
public interface ITagRepository
{
    /// <summary>Every tag, or only those of <paramref name="type"/>; order is not guaranteed.</summary>
    Task<IReadOnlyList<Tag>> GetTagsAsync(TagType? type, CancellationToken ct);
}
