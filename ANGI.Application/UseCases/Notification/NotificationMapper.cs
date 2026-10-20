using System.Text.Json;
using ANGI.Application.DTOs.Notification;

namespace ANGI.Application.UseCases.Notification;

/// <summary>Provides mapping helpers from Domain Notification entity to NotificationDto.</summary>
public static class NotificationMapper
{
    public static NotificationDto Map(Domain.Entities.Notification entity)
    {
        object dataObject;
        try
        {
            dataObject = JsonSerializer.Deserialize<JsonElement>(
                string.IsNullOrWhiteSpace(entity.Data) ? "{}" : entity.Data);
        }
        catch
        {
            dataObject = new { };
        }

        return new NotificationDto
        {
            Id = entity.Id,
            Type = entity.Type,
            Title = entity.Title,
            Body = entity.Body,
            Data = dataObject,
            ReadAt = entity.ReadAt,
            CreatedAt = entity.CreatedAt
        };
    }
}
