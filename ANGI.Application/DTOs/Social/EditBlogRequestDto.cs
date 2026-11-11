using System.Text.Json.Serialization;

namespace ANGI.Application.DTOs.Social;

/// <summary>Contains optional update fields accepted by BLOG-06.</summary>
public sealed class EditBlogRequestDto
{
    private string? _title;
    private string? _content;
    private long? _coverMediaId;
    private IReadOnlyList<long>? _imageMediaIds;
    private bool? _refreshRoadmapSnapshot;

    public string? Title
    {
        get => _title;
        set
        {
            _title = value;
            TitleSpecified = true;
        }
    }

    public string? Content
    {
        get => _content;
        set
        {
            _content = value;
            ContentSpecified = true;
        }
    }

    public long? CoverMediaId
    {
        get => _coverMediaId;
        set
        {
            _coverMediaId = value;
            CoverMediaIdSpecified = true;
        }
    }

    public IReadOnlyList<long>? ImageMediaIds
    {
        get => _imageMediaIds;
        set
        {
            _imageMediaIds = value;
            ImageMediaIdsSpecified = true;
        }
    }

    public bool? RefreshRoadmapSnapshot
    {
        get => _refreshRoadmapSnapshot;
        set
        {
            _refreshRoadmapSnapshot = value;
            RefreshRoadmapSnapshotSpecified = true;
        }
    }

    [JsonIgnore]
    public bool TitleSpecified { get; private set; }

    [JsonIgnore]
    public bool ContentSpecified { get; private set; }

    [JsonIgnore]
    public bool CoverMediaIdSpecified { get; private set; }

    [JsonIgnore]
    public bool ImageMediaIdsSpecified { get; private set; }

    [JsonIgnore]
    public bool RefreshRoadmapSnapshotSpecified { get; private set; }
}
