using System.Text.Json.Serialization;

namespace ANGI.Application.DTOs.Restaurant;

/// <summary>Represents a proposed dish in a menu submission.</summary>
public sealed class MenuSubmissionItemDto
{
    public long Id { get; set; }
    public int? SourceDishId { get; set; }
    public string Change { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string DishForm { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public long? CoverMediaId { get; set; }
    public string? CoverUrl { get; set; }
    public IReadOnlyList<short> TagIds { get; set; } = Array.Empty<short>();
    public short SortOrder { get; set; }
    public int? ResultDishId { get; set; }
}

/// <summary>Contains the required proposed-dish fields accepted by OWN-14.</summary>
public sealed class AddMenuSubmissionItemRequestDto
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string DishForm { get; set; } = string.Empty;
    public decimal? Price { get; set; }
    public long? CoverMediaId { get; set; }
    public IReadOnlyList<short>? TagIds { get; set; }
    public short? SortOrder { get; set; }
}

/// <summary>Contains fields that may be replaced by OWN-15.</summary>
public sealed class UpdateMenuSubmissionItemRequestDto
{
    private string? _name;
    private string? _description;
    private string? _dishForm;
    private decimal? _price;
    private long? _coverMediaId;
    private IReadOnlyList<short>? _tagIds;
    private short? _sortOrder;

    public string? Name { get => _name; set { _name = value; NameSpecified = true; } }
    public string? Description { get => _description; set { _description = value; DescriptionSpecified = true; } }
    public string? DishForm { get => _dishForm; set { _dishForm = value; DishFormSpecified = true; } }
    public decimal? Price { get => _price; set { _price = value; PriceSpecified = true; } }
    public long? CoverMediaId
    {
        get => _coverMediaId;
        set
        {
            _coverMediaId = value;
            CoverMediaIdSpecified = true;
        }
    }
    public IReadOnlyList<short>? TagIds { get => _tagIds; set { _tagIds = value; TagIdsSpecified = true; } }
    public short? SortOrder { get => _sortOrder; set { _sortOrder = value; SortOrderSpecified = true; } }

    [JsonIgnore] public bool NameSpecified { get; private set; }
    [JsonIgnore] public bool DescriptionSpecified { get; private set; }
    [JsonIgnore] public bool DishFormSpecified { get; private set; }
    [JsonIgnore] public bool PriceSpecified { get; private set; }
    [JsonIgnore]
    public bool CoverMediaIdSpecified { get; private set; }
    [JsonIgnore] public bool TagIdsSpecified { get; private set; }
    [JsonIgnore] public bool SortOrderSpecified { get; private set; }
}
