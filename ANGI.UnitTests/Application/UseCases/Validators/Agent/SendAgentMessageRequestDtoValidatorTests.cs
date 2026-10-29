using ANGI.Application.Common.Models.Agent;
using ANGI.Application.DTOs.Agent;
using ANGI.Application.UseCases.Validators.Agent;
using FluentAssertions;

namespace ANGI.UnitTests.Application.UseCases.Validators.Agent;

/// <summary>Verifies idempotency identity and message transport boundaries.</summary>
public sealed class SendAgentMessageRequestDtoValidatorTests
{
    // TEST-01: Reject explicit non-positive identity/version, null content, oversized text and invalid commands or selections.
    /// <summary>Checks every admission field before persistence or provider calls.</summary>
    [Theory]
    [InlineData("id")][InlineData("negative-id")][InlineData("min-id")][InlineData("version")][InlineData("null")][InlineData("empty")][InlineData("long")]
    [InlineData("selection-empty")][InlineData("selection-long")][InlineData("command-null")][InlineData("command-invalid")][InlineData("state")]
    public void Validate_InvalidTransport_Fails(string area)
    {
        var request = Valid();
        switch (area)
        {
            case "id": request.ClientMessageId = 0; break;
            case "negative-id": request.ClientMessageId = -1; break;
            case "min-id": request.ClientMessageId = long.MinValue; break;
            case "version": request.ExpectedConversationVersion = 0; break;
            case "null": request.Content = null!; break;
            case "empty": request.Content = " "; break;
            case "long": request.Content = new('x', 4001); break;
            case "selection-empty": request.SelectedItemKey = " "; break;
            case "selection-long": request.SelectedItemKey = new('x', 101); break;
            case "command-null": request.Command = null!; break;
            case "command-invalid": request.Command = "delete_roadmap"; break;
            default: request.ConfirmedState = new AgentState { NumDays = 31 }; break;
        }
        new SendAgentMessageRequestDtoValidator(new AgentLimits()).Validate(request).IsValid.Should().BeFalse();
    }

    // TEST-02: Accept inclusive text/key limits and each explicit non-destructive command.
    /// <summary>Verifies allowed message commands and exact maximum lengths.</summary>
    [Theory]
    [InlineData("chat")][InlineData("alternative")][InlineData("discard")][InlineData("reset")]
    public void Validate_AllowedCommand_Succeeds(string command)
    {
        var request = Valid(); request.Command = command; request.Content = new('x', 4000); request.SelectedItemKey = new('x', 100);
        new SendAgentMessageRequestDtoValidator(new AgentLimits()).Validate(request).IsValid.Should().BeTrue();
    }

    // TEST-04: Request-supplied state cannot forge a pending approval, source version or reset history marker.
    /// <summary>Keeps the metadata that authorizes chat lifecycle transitions exclusively server-controlled.</summary>
    [Theory]
    [InlineData("pending")]
    [InlineData("version")]
    [InlineData("history")]
    public void Validate_ForgedBackendMetadata_Fails(string field)
    {
        var state = new AgentState();
        var request = Valid();
        request.ConfirmedState = field switch { "pending" => state with { PendingStateChange = state },
            "version" => state with { SourceRoadmapVersion = 1 }, _ => state with { HistoryStartSequence = 1 } };
        new SendAgentMessageRequestDtoValidator(new AgentLimits()).Validate(request).IsValid.Should().BeFalse();
    }

    /// <summary>Creates the smallest valid message envelope.</summary>
    private static SendAgentMessageRequestDto Valid() => new() { ClientMessageId = 1, ExpectedConversationVersion = 1, Content = "x" };

    // TEST-03: Accept the smallest and largest positive 64-bit client message identifiers.
    /// <summary>Checks numeric identity boundaries independently of the message length and command rules.</summary>
    [Theory]
    [InlineData(1L)]
    [InlineData(long.MaxValue)]
    public void Validate_PositiveNumericIdentity_Succeeds(long clientMessageId)
    {
        var request = Valid(); request.ClientMessageId = clientMessageId;
        new SendAgentMessageRequestDtoValidator(new AgentLimits()).Validate(request).IsValid.Should().BeTrue();
    }

    // TEST-05: Prompt-only and partially explicit requests may omit transport metadata or send null.
    /// <summary>Accepts server allocation without weakening validation of supplied positive identifiers.</summary>
    [Theory]
    [InlineData(null, null)]
    [InlineData(1L, null)]
    [InlineData(null, 1L)]
    public void Validate_OptionalIdentityAndVersion_Succeeds(long? messageId, long? version)
    {
        var request = new SendAgentMessageRequestDto { Content = "Tôi muốn ăn ở Việt Nam", ClientMessageId = messageId, ExpectedConversationVersion = version };
        new SendAgentMessageRequestDtoValidator(new AgentLimits()).Validate(request).IsValid.Should().BeTrue();
    }
}
