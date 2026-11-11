using System.Text.Json;
using System.Text.Json.Nodes;
using ANGI.Application.Common.Models.Agent;
using FluentAssertions;

namespace ANGI.UnitTests.Application.Common.Models.Agent;

/// <summary>Verifies the backend re-check of strict LLM schemas and the safe diagnostic path reported for a mismatch.</summary>
public sealed class AgentLlmSchemaTests
{
    // TEST-01: A complete strict output is valid and has no mismatch path, including explicit nulls and empty arrays.
    /// <summary>Ensures valid provider output passes both the boolean check and the diagnostic walk.</summary>
    [Fact]
    public void FindMismatch_ValidOutput_ReturnsNull()
    {
        // STEP-01: Read document from the response; assert AgentLlmSchema.IsValid() is true and AgentLlmSchema.FindMismatch() is null.
        using var document = JsonDocument.Parse(Output().ToJsonString());

        AgentLlmSchema.IsValid(document.RootElement, AgentLlmSchema.Output).Should().BeTrue();
        AgentLlmSchema.FindMismatch(document.RootElement, AgentLlmSchema.Output).Should().BeNull();
    }

    // TEST-02: Each mismatch kind reports the first failing path; covers missing, unexpected, wrong type, enum, nullable object type,
    // nested missing field inside a nullable object, array item field, non-array value, non-integer number and a value that is not an object.
    /// <summary>Checks that the reported path pinpoints the failing node so a schema repair can be targeted.</summary>
    [Theory]
    [InlineData("missing", "$.reply(missing)")]
    [InlineData("unexpected", "$.userId(unexpected)")]
    [InlineData("type", "$.reply")]
    [InlineData("enum", "$.action")]
    [InlineData("nullable-object-type", "$.state")]
    [InlineData("nested-missing", "$.state.schemaVersion(missing)")]
    [InlineData("array-item", "$.items[1].mealSlot")]
    [InlineData("not-array", "$.items")]
    [InlineData("not-integer", "$.baseProposalVersion")]
    [InlineData("root-array", "$")]
    public void FindMismatch_InvalidOutput_ReportsFirstFailingPath(string area, string expected)
    {
        // STEP-01: Get output from Output, derive root from output and apply the case selected by area.
        var output = Output();
        JsonNode root = output;
        switch (area)
        {
            case "missing": output.Remove("reply"); break;
            case "unexpected": output["userId"] = 99; break;
            case "type": output["reply"] = 5; break;
            case "enum": output["action"] = "delete_roadmap"; break;
            case "nullable-object-type": output["state"] = "text"; break;
            case "nested-missing": output["state"] = new JsonObject(); break;
            case "array-item": output["items"] = new JsonArray(Item("a", "lunch"), Item("b", "brunch")); break;
            case "not-array": output["items"] = new JsonObject(); break;
            case "not-integer": output["baseProposalVersion"] = 1.5; break;
            case "root-array": root = new JsonArray(); break;
        }
        // STEP-02: Read document from the response; assert AgentLlmSchema.IsValid() is false and AgentLlmSchema.FindMismatch() is expected.
        using var document = JsonDocument.Parse(root.ToJsonString());

        AgentLlmSchema.IsValid(document.RootElement, AgentLlmSchema.Output).Should().BeFalse();
        AgentLlmSchema.FindMismatch(document.RootElement, AgentLlmSchema.Output).Should().Be(expected);
    }

    // TEST-03: Unexpected field names are logged only when short identifier-like names; covers 40 and 41 characters, spaces and symbols.
    /// <summary>Prevents provider text from reaching logs through an invented property name.</summary>
    [Theory]
    [InlineData("a234567890123456789012345678901234567890", "$.a234567890123456789012345678901234567890(unexpected)")]
    [InlineData("a2345678901234567890123456789012345678901", "$.?(unexpected)")]
    [InlineData("private prompt", "$.?(unexpected)")]
    [InlineData("key-with-dash", "$.?(unexpected)")]
    [InlineData("snake_case_1", "$.snake_case_1(unexpected)")]
    public void FindMismatch_UnexpectedName_MasksUnsafeNames(string name, string expected)
    {
        // STEP-01: Get output from Output and set output[name] to 1.
        var output = Output(); output[name] = 1;
        // STEP-02: Read document from the response; assert AgentLlmSchema.FindMismatch() is expected.
        using var document = JsonDocument.Parse(output.ToJsonString());

        AgentLlmSchema.FindMismatch(document.RootElement, AgentLlmSchema.Output).Should().Be(expected);
    }

    // TEST-04: A repeated property is reported as a duplicate even though each copy is individually valid.
    /// <summary>Keeps the diagnostic walk consistent with the boolean check for JSON with duplicate keys.</summary>
    [Fact]
    public void FindMismatch_DuplicateProperty_ReportsDuplicate()
    {
        // STEP-01: Derive json from the expected value.
        var json = Output().ToJsonString().TrimEnd('}') + ",\"action\":\"answer\"}";
        // STEP-02: Read document from the response; assert AgentLlmSchema.IsValid() is false and AgentLlmSchema.FindMismatch() is "$(duplicate)".
        using var document = JsonDocument.Parse(json);

        AgentLlmSchema.IsValid(document.RootElement, AgentLlmSchema.Output).Should().BeFalse();
        AgentLlmSchema.FindMismatch(document.RootElement, AgentLlmSchema.Output).Should().Be("$(duplicate)");
    }

    // TEST-05: Tool argument schemas use the same walk; covers a valid call, a missing required field and a wrong scalar type.
    /// <summary>Verifies tool calls are diagnosed with the same path format as final outputs.</summary>
    [Theory]
    [InlineData("{\"roadmapId\":88}", null)]
    [InlineData("{}", "$.roadmapId(missing)")]
    [InlineData("{\"roadmapId\":\"88\"}", "$.roadmapId")]
    public void FindMismatch_ToolArguments_UsesSamePathFormat(string arguments, string? expected)
    {
        // STEP-01: Read document from the response; assert AgentLlmSchema.FindMismatch() is expected.
        using var document = JsonDocument.Parse(arguments);

        AgentLlmSchema.FindMismatch(document.RootElement, AgentLlmSchema.Tools["get_roadmap"]).Should().Be(expected);
    }

    // TEST-06: The social and refusal actions are allowed, and suggestedDish is a required nullable string; covers both actions, null, text, a wrong type and a missing field.
    /// <summary>Keeps the new actions inside the strict contract so a malformed refusal is repaired rather than shown.</summary>
    [Theory]
    [InlineData("chat", "null", null)]
    [InlineData("out_of_scope", "\"phở\"", null)]
    [InlineData("out_of_scope", "5", "$.suggestedDish")]
    [InlineData("out_of_scope", "missing", "$.suggestedDish(missing)")]
    public void FindMismatch_SmallTalkActions_AreAllowed(string action, string dish, string? expected)
    {
        // STEP-01: Get output from Output, set output["action"] to action and when dish == "missing", call output.Remove.
        var output = Output(); output["action"] = action;
        if (dish == "missing") output.Remove("suggestedDish");
        else output["suggestedDish"] = JsonNode.Parse(dish);
        // STEP-02: Read document from the response; assert AgentLlmSchema.FindMismatch() is expected.
        using var document = JsonDocument.Parse(output.ToJsonString());

        AgentLlmSchema.FindMismatch(document.RootElement, AgentLlmSchema.Output).Should().Be(expected);
    }

    /// <summary>Builds a complete strict output with explicit nulls and empty arrays, matching the provider contract.</summary>
    private static JsonObject Output() => new()
    {
        ["action"] = "answer", ["reply"] = "Explanation", ["tool"] = null, ["pendingAnswer"] = null, ["answerStartDate"] = null, ["answerEndDate"] = null, ["suggestedDish"] = null, ["clarification"] = null, ["state"] = null,
        ["constraintsChangeConfirmed"] = false, ["baseProposalVersion"] = null,
        ["scope"] = new JsonObject { ["dayNumber"] = null, ["mealSlot"] = null }, ["editMeals"] = new JsonArray(),
        ["items"] = new JsonArray(), ["changes"] = new JsonArray(), ["dishNotes"] = new JsonArray(), ["daySummaries"] = new JsonArray()
    };

    /// <summary>Builds one strict proposal item for array-path cases.</summary>
    private static JsonObject Item(string key, string meal) => new()
    {
        ["itemKey"] = key, ["sourceItemId"] = null, ["dayNumber"] = 1, ["mealSlot"] = meal, ["sortOrder"] = 0, ["dishId"] = 1
    };
}
