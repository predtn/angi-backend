namespace ANGI.Application.DTOs.Discovery;

/// <summary>Represents a tag attached to a dish.</summary>
public sealed class TagDto
{
    public short Id { get; set; }
    public string TagType { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}
