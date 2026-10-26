using ANGI.Application.DTOs.Agent;
using ANGI.Application.UseCases.Validators.Agent;
using FluentAssertions;

namespace ANGI.UnitTests.Application.UseCases.Validators.Agent;

/// <summary>Verifies exact proposal identity admission.</summary>
public sealed class ConfirmAgentProposalRequestDtoValidatorTests
{
    // TEST-01: Require positive proposal IDs and versions at both numeric boundaries.
    /// <summary>Checks missing, negative and valid proposal references.</summary>
    [Theory]
    [InlineData(1L, 1, true)][InlineData(long.MaxValue, int.MaxValue, true)][InlineData(0L, 1, false)]
    [InlineData(-1L, 1, false)][InlineData(1L, 0, false)][InlineData(1L, -1, false)]
    public void Validate_ProposalReference_RequiresPositiveValues(long id, int version, bool valid) =>
        new ConfirmAgentProposalRequestDtoValidator().Validate(new ConfirmAgentProposalRequestDto { ProposalId = id, ProposalVersion = version }).IsValid.Should().Be(valid);
}
