using ANGI.Application.Services.Agent.Intake;
using FluentAssertions;

namespace ANGI.UnitTests.Application.Services.Agent.Intake;

/// <summary>Verifies the request being clarified keeps its messages for the model to read, and reads no figure from them itself.</summary>
public sealed class AgentPendingPlanningPolicyTests
{
    // TEST-01: Messages add up across turns, trimmed; a sentence that only looks like a budget stays a message and states nothing.
    /// <summary>"Trưa nay ăn nhẹ, 50k thôi" once replaced a whole trip's budget, because a pattern read the figure the model had read correctly.</summary>
    [Fact]
    public void Capture_KeepsMessages_AndReadsNoFigures()
    {
        // STEP-01: Get draft from AgentIntakePolicy.CaptureRequest, twice.
        var draft = AgentIntakePolicy.CaptureRequest(null, " Tôi đi Hà Nội 4 ngày, 500k/ngày ");
        draft = AgentIntakePolicy.CaptureRequest(draft, "trưa nay ăn nhẹ, 50k thôi");

        // STEP-02: Assert draft.Messages equals the messages and the figures stay empty.
        draft.Messages.Should().Equal("Tôi đi Hà Nội 4 ngày, 500k/ngày", "trưa nay ăn nhẹ, 50k thôi");
        draft.NumDays.Should().BeNull();
        draft.TotalBudget.Should().BeNull();
        draft.DailyBudget.Should().BeNull();
    }

    // TEST-02: A long clarification keeps its first message and the latest ones, up to six.
    /// <summary>Bounds the prompt without losing the request that started the conversation.</summary>
    [Fact]
    public void Capture_LongRequest_KeepsFirstAndLatest()
    {
        // STEP-01: Capture eight messages.
        var draft = AgentIntakePolicy.CaptureRequest(null, "m0");
        foreach (var index in Enumerable.Range(1, 7)) draft = AgentIntakePolicy.CaptureRequest(draft, $"m{index}");

        // STEP-02: Assert draft.Messages equals the first and the five latest.
        draft.Messages.Should().Equal("m0", "m3", "m4", "m5", "m6", "m7");
    }
}
