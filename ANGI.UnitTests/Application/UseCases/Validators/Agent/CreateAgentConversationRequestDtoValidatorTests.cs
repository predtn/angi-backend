using ANGI.Application.DTOs.Agent;
using ANGI.Application.UseCases.Validators.Agent;
using FluentAssertions;

namespace ANGI.UnitTests.Application.UseCases.Validators.Agent;

/// <summary>Verifies optional roadmap reference boundaries.</summary>
public sealed class CreateAgentConversationRequestDtoValidatorTests
{
    // TEST-01: Accept no roadmap or a positive ID and reject zero and negative IDs.
    /// <summary>Checks each optional ID equivalence class.</summary>
    [Theory]
    [InlineData(null, true)][InlineData(1L, true)][InlineData(long.MaxValue, true)][InlineData(0L, false)][InlineData(-1L, false)]
    public void Validate_RoadmapId_UsesPositiveBoundary(long? id, bool valid) =>
        new CreateAgentConversationRequestDtoValidator().Validate(new CreateAgentConversationRequestDto { RoadmapId = id }).IsValid.Should().Be(valid);
}
