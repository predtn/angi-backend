using System.Text.Json.Serialization;

namespace ANGI.Application.DTOs.Restaurant;

/// <summary>Contains the optional restaurant profile fields accepted by OWN-03.</summary>
public sealed class UpdateRestaurantRequestDto
{
    private string? _name;
    private string? _description;
    private string? _phone;
    private string? _email;
    private string? _website;
    private string? _addressLine;
    private string? _ward;
    private string? _district;
    private string? _provinceName;
    private decimal? _latitude;
    private decimal? _longitude;
    private short? _priceLevel;
    private long? _coverMediaId;
    private IReadOnlyList<long>? _imageMediaIds;

    public string? Name { get => _name; set { _name = value; NameSpecified = true; } }
    public string? Description { get => _description; set { _description = value; DescriptionSpecified = true; } }
    public string? Phone { get => _phone; set { _phone = value; PhoneSpecified = true; } }
    public string? Email { get => _email; set { _email = value; EmailSpecified = true; } }
    public string? Website { get => _website; set { _website = value; WebsiteSpecified = true; } }
    public string? AddressLine { get => _addressLine; set { _addressLine = value; AddressLineSpecified = true; } }
    public string? Ward { get => _ward; set { _ward = value; WardSpecified = true; } }
    public string? District { get => _district; set { _district = value; DistrictSpecified = true; } }
    public string? ProvinceName { get => _provinceName; set { _provinceName = value; ProvinceNameSpecified = true; } }
    public decimal? Latitude { get => _latitude; set { _latitude = value; LatitudeSpecified = true; } }
    public decimal? Longitude { get => _longitude; set { _longitude = value; LongitudeSpecified = true; } }
    public short? PriceLevel { get => _priceLevel; set { _priceLevel = value; PriceLevelSpecified = true; } }
    public long? CoverMediaId { get => _coverMediaId; set { _coverMediaId = value; CoverMediaIdSpecified = true; } }
    public IReadOnlyList<long>? ImageMediaIds { get => _imageMediaIds; set { _imageMediaIds = value; ImageMediaIdsSpecified = true; } }

    [JsonIgnore] public bool NameSpecified { get; private set; }
    [JsonIgnore] public bool DescriptionSpecified { get; private set; }
    [JsonIgnore] public bool PhoneSpecified { get; private set; }
    [JsonIgnore] public bool EmailSpecified { get; private set; }
    [JsonIgnore] public bool WebsiteSpecified { get; private set; }
    [JsonIgnore] public bool AddressLineSpecified { get; private set; }
    [JsonIgnore] public bool WardSpecified { get; private set; }
    [JsonIgnore] public bool DistrictSpecified { get; private set; }
    [JsonIgnore] public bool ProvinceNameSpecified { get; private set; }
    [JsonIgnore] public bool LatitudeSpecified { get; private set; }
    [JsonIgnore] public bool LongitudeSpecified { get; private set; }
    [JsonIgnore] public bool PriceLevelSpecified { get; private set; }
    [JsonIgnore] public bool CoverMediaIdSpecified { get; private set; }
    [JsonIgnore] public bool ImageMediaIdsSpecified { get; private set; }
}
