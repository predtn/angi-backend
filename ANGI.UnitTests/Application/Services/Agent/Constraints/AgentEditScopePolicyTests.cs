using FluentAssertions;
using ANGI.Application.Services.Agent.Constraints;
using ANGI.Application.Services.Agent;

namespace ANGI.UnitTests.Application.Services.Agent.Constraints;

/// <summary>Verifies edits that name nothing are clarified by the backend while every edit with a real target still reaches the model.</summary>
public sealed class AgentEditScopePolicyTests
{
    // TEST-01: Pointing words are not catalog names; covers "cái này", "món đó", a real dish and empty text.
    /// <summary>Stops a lookup that could only report a false "not found".</summary>
    [Theory]
    [InlineData("cái này", true)]
    [InlineData("món đó", true)]
    [InlineData("Phở Gà", false)]
    [InlineData("  ", false)]
    public void IsPointingOnly_ReadsWords(string name, bool expected)
    {
        // STEP-01: Assert AgentToolExecutor.IsPointingOnly() is expected.
        AgentToolExecutor.IsPointingOnly(name).Should().Be(expected);
    }

}
