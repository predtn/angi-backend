using System.Text.Json.Serialization;

namespace ANGI.Application.DTOs.Restaurant;

/// <summary>Encapsulates the request body for OWN-20 owner review reply.</summary>
public sealed class ReplyReviewRequestDto
{
    private string _content = string.Empty;

    public string Content
    {
        get => _content;
        init => _content = value ?? string.Empty;
    }

    [JsonPropertyName("replyContent")]
    public string? ReplyContent
    {
        get => _content;
        init
        {
            if (!string.IsNullOrEmpty(value))
            {
                _content = value;
            }
        }
    }
}
