using System.Text.Json.Serialization;

namespace ANGI.Application.DTOs.Account;

/// <summary>Contains optional personal profile fields accepted by ME-02.</summary>
public sealed class UpdateProfileRequestDto
{
    private string? _displayName;
    private string? _phone;
    private string? _bio;
    private long? _avatarMediaId;

    public string? DisplayName
    {
        get => _displayName;
        set
        {
            _displayName = value;
            DisplayNameSpecified = true;
        }
    }

    public string? Phone
    {
        get => _phone;
        set
        {
            _phone = value;
            PhoneSpecified = true;
        }
    }

    public string? Bio
    {
        get => _bio;
        set
        {
            _bio = value;
            BioSpecified = true;
        }
    }

    public long? AvatarMediaId
    {
        get => _avatarMediaId;
        set
        {
            _avatarMediaId = value;
            AvatarMediaIdSpecified = true;
        }
    }

    [JsonIgnore]
    public bool DisplayNameSpecified { get; private set; }

    [JsonIgnore]
    public bool PhoneSpecified { get; private set; }

    [JsonIgnore]
    public bool BioSpecified { get; private set; }

    [JsonIgnore]
    public bool AvatarMediaIdSpecified { get; private set; }
}
