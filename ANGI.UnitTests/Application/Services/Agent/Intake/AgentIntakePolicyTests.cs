using ANGI.Application.Common.Models.Agent;
using ANGI.Application.DTOs.Agent;
using ANGI.Application.Services.Agent.Intake;
using FluentAssertions;

namespace ANGI.UnitTests.Application.Services.Agent.Intake;

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

    // TEST-06: Tools close only for a new plan with no special request whose search covered every meal; covers each reason to keep them open.
    /// <summary>Ensures the saving from conversation 94 never costs a side dish or named request its extra search.</summary>
    [Theory]
    [InlineData("covered", true)][InlineData("not-intake", false)][InlineData("no-meals", false)][InlineData("meal-request", false)]
    [InlineData("gap", false)][InlineData("uncovered-day", false)][InlineData("taste-answer", false)][InlineData("side-dish", false)][InlineData("earlier-request", false)]
    [InlineData("quick-reply", true)][InlineData("taste-only", true)][InlineData("sightseeing", true)]
    public void PlanCovered_OnlyWithoutAnythingLeftToSearch(string area, bool expected)
    {
        var context = Context(area switch { "taste-answer" => "không ăn cay", "side-dish" => "thêm trà sữa nhé", "quick-reply" => "Không, lập lịch luôn nhé.", "taste-only" => "Cay nhẹ cho toàn chuyến đi", _ => "không" });
        context.IntakeComplete = area != "not-intake";
        context.State = context.State with
        {
            NumDays = 2, MealSlots = area == "no-meals" ? [] : ["lunch"],
            MealRequests = area == "meal-request" ? [new(1, "lunch", "Phở", null, [])] : [],
            PendingPlanningRequest = area == "earlier-request" ? new(["ngày 1 ăn hải sản"]) : area == "sightseeing" ? new(["Ngày 1 tôi đi tham quan Hồ Gươm ở quận Hoàn Kiếm"]) : null
        };
        context.MealCoverage.Add(new(1, "lunch", area == "gap" ? 0 : 3, "", []));
        if (area != "uncovered-day") context.MealCoverage.Add(new(2, "lunch", 3, "", []));

        AgentIntakePolicy.PlanCovered(context).Should().Be(expected);
    }

    // TEST-07: An itinerary opens the questions without planning wording, skips the place question and is acknowledged; a day past the known duration grows it.
    /// <summary>Ensures "Ngày 1 ở Hoàn Kiếm" alone is treated as the start of a plan and reaches the user as a confirmation.</summary>
    [Fact]
    public void Read_Itinerary_StartsIntakeAndCarriesPlaces()
    {
        AgentDayArea[] areas = [new(1, "Hoàn Kiếm", 21.03m, 105.85m, 3m), new(2, "Lăng Bác", 21.04m, 105.83m, 3m)];
        var read = AgentIntakePolicy.Read(Context("Ngày 1 ở Hoàn Kiếm, ngày 2 đi Lăng Bác"), Provinces, null, new("Hà Nội", areas, null, [], [], 2))!;

        read.Progress.Should().BeTrue();
        read.Current.DayAreas.Should().Equal(areas);
        read.Proposed.Should().Match<AgentState>(x => x.Province == "Hà Nội" && x.NumDays == 2 && x.PlanningIntake == AgentIntakePolicy.Details);
        read.Note.Should().Contain("ngày 1 quanh Hoàn Kiếm").And.Contain("ngày 2 quanh Lăng Bác");

        var step = AgentIntakePolicy.Next(Context("Ngày 1 ở Hoàn Kiếm, ngày 2 đi Lăng Bác"), read.Proposed, read, Provinces);
        step.Reply.Should().StartWith("Mình ghi nhận hành trình ở Hà Nội").And.Contain("ngân sách");
    }

    // TEST-08: A whole-trip area keeps its origin and radius through the read instead of being reset by the old state.
    /// <summary>Ensures "ở Cầu Giấy" stays a search around Cầu Giấy all the way into the proposed state.</summary>
    [Fact]
    public void Read_TripArea_KeepsOriginAndRadius()
    {
        var read = AgentIntakePolicy.Read(Context("lập lịch ở Cầu Giấy"), Provinces, null, new("Hà Nội", [], new("Hà Nội", "Cầu Giấy", 21.03m, 105.79m, 3m), [], [], 0))!;

        read.Proposed.Should().Match<AgentState>(x => x.OriginMode == "pinned" && x.OriginLabel == "Cầu Giấy" && x.RadiusKm == 3m && x.Province == "Hà Nội");
        read.Note.Should().Contain("khu vực Cầu Giấy");
    }

    // TEST-09: An itinerary sent with the answer to the preferences question still applies, and planning still starts.
    /// <summary>Ensures a late itinerary is not lost because the intake was already closing.</summary>
    [Fact]
    public void Read_ItineraryAtPreferencesStage_AppliesAndCloses()
    {
        var context = Context("Ngày 1 ở Hoàn Kiếm");
        context.State = context.State with { Province = "Hà Nội", NumDays = 2, PlanningIntake = AgentIntakePolicy.Preferences };

        var read = AgentIntakePolicy.Read(context, Provinces, null, new("Hà Nội", [new(1, "Hoàn Kiếm", 21.03m, 105.85m, 3m)], null, [], [], 1))!;

        read.Proposed.DayAreas.Should().ContainSingle();
        var step = AgentIntakePolicy.Next(context, read.Proposed, read, Provinces);
        step.Plan.Should().BeTrue();
        step.State.DayAreas.Should().ContainSingle();
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
