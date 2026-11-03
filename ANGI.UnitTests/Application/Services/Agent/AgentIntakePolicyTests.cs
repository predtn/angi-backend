using ANGI.Application.Common.Models.Agent;
using ANGI.Application.DTOs.Agent;
using ANGI.Application.Services.Agent;
using FluentAssertions;

namespace ANGI.UnitTests.Application.Services.Agent;

/// <summary>Verifies AgentIntakePolicy decides when the pre-plan questions run and reads each answer without a provider call.</summary>
public sealed class AgentIntakePolicyTests
{
    private static readonly string[] Provinces = ["Hà Nội", "TP. Hồ Chí Minh"];

    // TEST-01: Planning wording and a bare place start the questions; covers accents, no accents and the wording the user reported.
    /// <summary>Ensures a new plan request is recognized even when the user types without accents.</summary>
    [Theory]
    [InlineData("lap lich cho toi")][InlineData("Lập lịch cho tôi")][InlineData("lên kế hoạch giúp mình")][InlineData("tạo lộ trình")]
    [InlineData("tôi muốn trải nghiệm ẩm thực")][InlineData("Hà Nội")][InlineData("ha noi nhe")]
    public void Read_NewPlanWording_StartsIntake(string content) =>
        AgentIntakePolicy.Read(Context(content), Provinces, null).Should().NotBeNull();

    // TEST-02: Messages that are not plan requests stay with the normal flow; covers a dish search, a place inside a dish question and small talk.
    /// <summary>Ensures the questions never hijack ordinary catalog requests.</summary>
    [Theory]
    [InlineData("tìm quán phở")][InlineData("Hà Nội có món gì ngon")][InlineData("thời tiết hôm nay")]
    public void Read_OtherMessage_ReturnsNull(string content) =>
        AgentIntakePolicy.Read(Context(content), Provinces, null).Should().BeNull();

    // TEST-03: A catalog province is matched by whole words; covers accents, no accents, short names and a word that only contains the name.
    /// <summary>Ensures short names resolve while unrelated text is never mistaken for a place.</summary>
    [Theory]
    [InlineData("ha noi", "Hà Nội")][InlineData("toi o hn", "Hà Nội")][InlineData("di sai gon", "TP. Hồ Chí Minh")][InlineData("tp hcm", "TP. Hồ Chí Minh")]
    [InlineData("ha noii", null)][InlineData("da nang", null)]
    public void FindProvince_MatchesWholeWordsAndAliases(string text, string? expected) =>
        AgentIntakePolicy.FindProvince(text, Provinces).Should().Be(expected);

    // TEST-04: A plan on screen, a staged change or a non-chat command all keep the existing flow.
    /// <summary>Ensures editing an existing plan never restarts the questions.</summary>
    [Fact]
    public void Eligible_ExistingPlanStagedChangeOrCommand_IsFalse()
    {
        AgentIntakePolicy.Eligible(Context("lập lịch")).Should().BeTrue();
        AgentIntakePolicy.Eligible(Context("lập lịch", AgentTestData.Snapshot(AgentTestData.Item()))).Should().BeFalse();
        AgentIntakePolicy.Eligible(Context("lập lịch", AgentTestData.Snapshot())).Should().BeTrue();
        var staged = Context("lập lịch");
        staged.State = staged.State with { PendingStateChange = new AgentState() };
        AgentIntakePolicy.Eligible(staged).Should().BeFalse();
        var command = Context("lập lịch");
        command.Request.Command = "reset";
        AgentIntakePolicy.Eligible(command).Should().BeFalse();
    }

    // TEST-05: A province only the roadmap title holds is dropped from a model's state; covers the title, a stated name, a known place, coordinates, another place and a null state.
    /// <summary>Ensures "3 ngày ăn ngon Hà Nội" in the title cannot answer the place question on the user's behalf.</summary>
    [Fact]
    public void WithoutUnstatedProvince_KeepsOnlyUserStatedPlace()
    {
        var titled = new AgentState { Province = "Hà Nội", Title = "3 ngày ăn ngon Hà Nội" };
        AgentContext Titled(string content)
        {
            var context = Context(content);
            context.State = context.State with { Title = titled.Title };
            return context;
        }
        AgentIntakePolicy.WithoutUnstatedProvince(titled, Titled("lập lịch cho tôi"))!.Province.Should().BeNull();
        AgentIntakePolicy.WithoutUnstatedProvince(titled, Titled("lập lịch ở ha noi"))!.Province.Should().Be("Hà Nội");
        AgentIntakePolicy.WithoutUnstatedProvince(titled with { Province = "Huế" }, Titled("lập lịch"))!.Province.Should().Be("Huế");
        AgentIntakePolicy.WithoutUnstatedProvince(null, Context("lập lịch")).Should().BeNull();
        var history = Titled("3 ngày nhé");
        history.History.Add(new("user", "Hà Nội"));
        AgentIntakePolicy.WithoutUnstatedProvince(titled, history)!.Province.Should().Be("Hà Nội");
        var known = Titled("lập lịch");
        known.State = known.State with { Province = "Hà Nội" };
        AgentIntakePolicy.WithoutUnstatedProvince(titled, known).Should().BeSameAs(titled);
        var located = Titled("lập lịch");
        located.State = located.State with { OriginMode = "gps", OriginLatitude = 21m, OriginLongitude = 105m };
        AgentIntakePolicy.WithoutUnstatedProvince(titled, located).Should().BeSameAs(titled);
    }

    /// <summary>Builds a context with no plan and an unknown place.</summary>
    private static AgentContext Context(string content, AgentSnapshot? source = null)
    {
        var context = AgentTestData.Context(source);
        context.State = new AgentState();
        context.Request.Content = content;
        return context;
    }
}
