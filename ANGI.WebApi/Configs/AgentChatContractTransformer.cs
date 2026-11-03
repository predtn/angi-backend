using System.Reflection;
using System.Text.Json.Nodes;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.DTOs.Agent;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace ANGI.WebApi.Configs;

/// <summary>Shows the chat-only request in Scalar while preserving compatible optional request fields.</summary>
internal sealed class AgentChatOperationTransformer : IOpenApiOperationTransformer
{
    /// <summary>Publishes a valid minimal example rather than empty commands and an unconfirmed state object.</summary>
    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        var path = context.Description.RelativePath;
        if (context.Description.HttpMethod == "POST" && path is not null &&
            path.StartsWith("api/v1/agent/conversations/", StringComparison.Ordinal) && path.EndsWith("/messages", StringComparison.Ordinal) &&
            operation.RequestBody?.Content?.TryGetValue("application/json", out var media) == true)
        {
            media.Example = JsonNode.Parse("""
                {"content":"Tôi muốn lên lịch ăn uống Hà Nội 1 ngày, ngân sách 500000 đồng"}
                """);
            operation.Description = "Chỉ cần gửi content. Bỏ clientMessageId và expectedConversationVersion để backend tự cấp ID tin mới và dùng version hiện tại. Nếu gửi ID rõ ràng: cùng ID/nội dung là retry; khác nội dung trả 409 MESSAGE_ID_REUSED. Nhắn ‘lưu lịch trình này’ để lưu preview; kết quả nằm trong data.commit.";
        }
        return Task.CompletedTask;
    }
}

/// <summary>Documents defaults and backend-owned state metadata without adding request validation attributes.</summary>
internal sealed class AgentChatSchemaTransformer : IOpenApiSchemaTransformer
{
    /// <summary>Marks server metadata read-only and supplies the existing chat default for compatible clients.</summary>
    public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        if (context.JsonPropertyInfo?.AttributeProvider is PropertyInfo property)
        {
            if (property.DeclaringType == typeof(AgentState) && property.Name is nameof(AgentState.PendingStateChange)
                or nameof(AgentState.SourceRoadmapUpdatedAt) or nameof(AgentState.HistoryStartSequence)) schema.ReadOnly = true;
            if (property.DeclaringType == typeof(SendAgentMessageRequestDto) && property.Name == nameof(SendAgentMessageRequestDto.Command))
                schema.Default = JsonValue.Create("chat");
        }
        return Task.CompletedTask;
    }
}
