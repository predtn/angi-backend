using System.Text.Json;

namespace ANGI.Application.Common.Models.Agent;

/// <summary>Defines the strict provider-independent output and tool allowlist contracts.</summary>
public static class AgentLlmSchema
{
    public static readonly object State = Obj(new()
    {
        ["schemaVersion"] = Scalar("integer"), ["province"] = Nullable(Scalar("string")),
        ["numDays"] = Nullable(Scalar("integer")), ["title"] = Scalar("string"),
        ["startDate"] = Nullable(Scalar("string")), ["budgetAmount"] = Nullable(Scalar("number")),
        ["budgetUnrestricted"] = Scalar("boolean"), ["maxPricePerMeal"] = Nullable(Scalar("number")),
        ["tagIds"] = ArrayOf(Scalar("integer")), ["excludeTagIds"] = ArrayOf(Scalar("integer")),
        ["originMode"] = Choice("none", "pinned", "gps"), ["originLatitude"] = Nullable(Scalar("number")),
        ["originLongitude"] = Nullable(Scalar("number")), ["originLabel"] = Nullable(Scalar("string")),
        ["radiusKm"] = Nullable(Scalar("number")), ["note"] = Nullable(Scalar("string")),
        ["pendingQuestion"] = Nullable(Scalar("string")), ["focusDay"] = Nullable(Scalar("integer")),
        ["focusMeal"] = Nullable(Choice("breakfast", "lunch", "dinner", "snack", "late_night"))
    });
    public static readonly object Scope = Obj(new()
    {
        ["dayNumber"] = Nullable(Scalar("integer")), ["mealSlot"] = Nullable(Choice("breakfast", "lunch", "dinner", "snack", "late_night"))
    });
    public static readonly object Item = Obj(new()
    {
        ["itemKey"] = Scalar("string"), ["sourceItemId"] = Nullable(Scalar("integer")), ["dayNumber"] = Scalar("integer"),
        ["mealSlot"] = Choice("breakfast", "lunch", "dinner", "snack", "late_night"), ["sortOrder"] = Scalar("integer"), ["dishId"] = Scalar("integer")
    });
    // Hợp đồng action của ANGI: clarify hỏi lại; answer trả lời; create_proposal dựng lịch mới;
    // patch_proposal sửa lịch nguồn. Model chọn theo Instructions và context, schema chỉ giới hạn dạng output.
    // Điều kiện được phép tạo/sửa và tính hợp lệ dữ liệu còn được Builder/StatePolicy/Validator kiểm tra.
    public static readonly object Output = Obj(new()
    {
        ["action"] = Choice("clarify", "answer", "create_proposal", "patch_proposal"), ["reply"] = Scalar("string"),
        ["state"] = Nullable(State), ["constraintsChangeConfirmed"] = Scalar("boolean"),
        ["baseProposalVersion"] = Nullable(Scalar("integer")), ["scope"] = Scope,
        ["items"] = ArrayOf(Item), ["changes"] = ArrayOf(Obj(new()
        {
            ["type"] = Choice("add", "replace", "remove", "move"), ["targetItemKey"] = Nullable(Scalar("string")), ["item"] = Nullable(Item)
        }))
    });
    public static readonly IReadOnlyDictionary<string, object> Tools = new Dictionary<string, object>
    {
        ["get_recommendation"] = Obj(new()
        {
            ["state"] = Nullable(State), ["province"] = Nullable(Scalar("string")), ["scope"] = Choice("full", "day", "slot"),
            ["dayNumber"] = Nullable(Scalar("integer")), ["mealSlot"] = Nullable(Choice("breakfast", "lunch", "dinner", "snack", "late_night")),
            ["tagIds"] = ArrayOf(Scalar("integer")), ["excludeTagIds"] = ArrayOf(Scalar("integer")), ["excludeDishIds"] = ArrayOf(Scalar("integer")),
            ["maxPricePerMeal"] = Nullable(Scalar("number")), ["radiusKm"] = Nullable(Scalar("number")), ["topK"] = Scalar("integer")
        }),
        ["estimate_day_route"] = Obj(new()
        {
            ["dayNumber"] = Scalar("integer"), ["profile"] = Choice("driving", "walking", "cycling"),
            ["orderedItems"] = ArrayOf(Obj(new() { ["itemKey"] = Scalar("string"), ["dishId"] = Scalar("integer") }))
        }),
        ["get_roadmap"] = Obj(new() { ["roadmapId"] = Scalar("integer") })
    };

    /// <summary>Builds an object schema whose properties are all required and whose extra fields are rejected.</summary>
    private static object Obj(Dictionary<string, object> properties) => new { type = "object", properties, required = properties.Keys.ToArray(), additionalProperties = false };
    /// <summary>Builds a primitive schema.</summary>
    private static object Scalar(string type) => new { type };
    /// <summary>Builds an allowlisted string enum.</summary>
    private static object Choice(params string[] values) => new Dictionary<string, object> { ["type"] = "string", ["enum"] = values };
    /// <summary>Builds a structured array schema.</summary>
    private static object ArrayOf(object items) => new { type = "array", items };
    /// <summary>Allows an optional value without omitting the required property in strict mode.</summary>
    private static object Nullable(object schema) => new { anyOf = new[] { schema, Scalar("null") } };

    /// <summary>Checks the strict schema again in backend code rather than trusting an upstream 200 response.</summary>
    public static bool IsValid(JsonElement value, object schema) => Match(value, JsonSerializer.SerializeToElement(schema));

    /// <summary>Recursively checks required fields, primitive types, arrays and enum membership.</summary>
    private static bool Match(JsonElement value, JsonElement schema)
    {
        if (schema.TryGetProperty("anyOf", out var options)) return options.EnumerateArray().Any(option => Match(value, option));
        var type = schema.GetProperty("type").GetString();
        if (type == "null") return value.ValueKind == JsonValueKind.Null;
        if (type == "string") return value.ValueKind == JsonValueKind.String && (!schema.TryGetProperty("enum", out var choices) || choices.EnumerateArray().Any(x => x.GetString() == value.GetString()));
        if (type == "integer") return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _);
        if (type == "number") return value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out _);
        if (type == "boolean") return value.ValueKind is JsonValueKind.True or JsonValueKind.False;
        if (type == "array") return value.ValueKind == JsonValueKind.Array && value.GetArrayLength() <= 2000 && value.EnumerateArray().All(x => Match(x, schema.GetProperty("items")));
        if (type != "object" || value.ValueKind != JsonValueKind.Object) return false;
        var properties = schema.GetProperty("properties");
        var fields = value.EnumerateObject().ToArray();
        return fields.Length == fields.Select(x => x.Name).Distinct().Count() && fields.Length == properties.EnumerateObject().Count() &&
            properties.EnumerateObject().All(x => value.TryGetProperty(x.Name, out var child) && Match(child, x.Value));
    }
}
