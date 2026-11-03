using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Services.Agent;
using FluentAssertions;

namespace ANGI.UnitTests.Application.Services.Agent;

/// <summary>Verifies that model wording about dishes and days reaches a preview only when it matches the final, verified dishes.</summary>
public sealed class AgentProposalNotesTests
{
    private static readonly AgentItem Quang = AgentTestData.Item(1, "a", 1, "lunch") with { DishName = "Mì Quảng", RestaurantName = "Quán Hương", Price = 50000 };
    private static readonly AgentItem Cha = AgentTestData.Item(2, "b", 1, "dinner") with { DishName = "Bún Chả", RestaurantName = "Quán Lan", Price = 30000 };
    private static readonly AgentItem Com = AgentTestData.Item(3, "c", 2, "lunch") with { DishName = "Cơm Tấm", RestaurantName = "Quán Bảy", Price = 40000 };

    private static AgentContext Context(AgentSnapshot? source = null, AgentDishNote[]? notes = null, AgentDaySummary[]? days = null)
    {
        var context = AgentTestData.Context(source);
        context.PreviewOutput = new AgentOutput { Action = "create_proposal", Reply = "Xong rồi nhé!", DishNotes = notes ?? [], DaySummaries = days ?? [] };
        return context;
    }

    // TEST-01: Natural wording about the final dishes is kept as written.
    /// <summary>This is the point of the feature: each dish and each day explains itself.</summary>
    [Fact]
    public void Attach_VerifiableWording_IsKept()
    {
        var result = AgentProposalNotes.Attach(AgentTestData.Snapshot(Quang, Cha), Context(
            notes: [new(1, "Mì Quảng có sợi mì vàng và nước dùng sánh.", "Hợp bữa trưa nhẹ mà vẫn no.")],
            days: [new(1, "Ngày này đi từ món nước sang món nướng nên không bị ngán.")]));

        result.DishNotes.Should().ContainSingle().Which.Should().Be(new AgentDishNote(1, "Mì Quảng có sợi mì vàng và nước dùng sánh.", "Hợp bữa trưa nhẹ mà vẫn no."));
        result.DaySummaries.Should().ContainSingle().Which.Summary.Should().StartWith("Ngày này");
    }

    // TEST-02: Wording the backend cannot verify is dropped; covers an invented price, an unknown name, fame, hours, internal terms and excess length.
    /// <summary>A missing sentence is invisible to the user; a false fact in a travel plan is not.</summary>
    [Theory]
    [InlineData("Món này chỉ 45.000 đồng thôi.")]
    [InlineData("Ngon hơn hẳn ở Quán Bà Tám gần đó.")]
    [InlineData("Đây là quán nổi tiếng nhất vùng.")]
    [InlineData("Quán này mở cửa đến khuya.")]
    [InlineData("Theo catalog thì món này hợp.")]
    [InlineData("Plain English sentence only.")]
    public void Attach_UnverifiableWording_IsDropped(string text)
    {
        var result = AgentProposalNotes.Attach(AgentTestData.Snapshot(Quang), Context(notes: [new(1, text, text)], days: [new(1, text)]));

        result.DishNotes.Should().BeEmpty();
        result.DaySummaries.Should().BeEmpty();
    }

    // TEST-03: One bad sentence does not cost the good one.
    /// <summary>Keeps what can be verified from a partly invented note.</summary>
    [Fact]
    public void Attach_OneBadSentence_KeepsTheOther()
    {
        var result = AgentProposalNotes.Attach(AgentTestData.Snapshot(Quang), Context(notes: [new(1, "Sợi mì vàng, nước dùng sánh.", "Chỉ 45.000 đồng.")]));

        result.DishNotes.Should().ContainSingle().Which.Should().Be(new AgentDishNote(1, "Sợi mì vàng, nước dùng sánh.", null));
    }

    // TEST-04: A note about a dish the backend swapped out afterwards never shows.
    /// <summary>The notes run after the repairs, so the card cannot describe a dish that is no longer there.</summary>
    [Fact]
    public void Attach_DishNoLongerInPreview_IsDropped()
    {
        var result = AgentProposalNotes.Attach(AgentTestData.Snapshot(Quang), Context(notes: [new(9, "Món đã bị thay ra.", "Không còn trong lịch.")]));

        result.DishNotes.Should().BeEmpty();
    }

    // TEST-05: A day summary may name only that day's dishes, so one written before a swap is not shown.
    /// <summary>Stops a conclusion that praises a dish from another day or one that was replaced.</summary>
    [Fact]
    public void Attach_SummaryNamingAnotherDaysDish_IsDropped()
    {
        var result = AgentProposalNotes.Attach(AgentTestData.Snapshot(Quang, Com), Context(days: [new(1, "Mở đầu nhẹ nhàng rồi kết bằng Cơm Tấm.")]));

        result.DaySummaries.Should().BeEmpty();
    }

    // TEST-06: An edit keeps what it did not touch: other dishes' notes and unchanged days' summaries stay, a changed day's summary goes.
    /// <summary>A small edit does not make the rest of the card go blank, nor leave a conclusion about dishes that left.</summary>
    [Fact]
    public void Attach_Edit_KeepsOnlyWhatStillApplies()
    {
        var source = AgentTestData.Snapshot(Quang, Com) with
        {
            DishNotes = [new(1, "Giới thiệu cũ.", "Lý do cũ."), new(3, "Giới thiệu cơm.", "Lý do cơm.")],
            DaySummaries = [new(1, "Kết luận ngày đầu."), new(2, "Kết luận ngày hai.")]
        };

        var result = AgentProposalNotes.Attach(AgentTestData.Snapshot(Cha, Com), Context(source, notes: [new(2, "Bún chả nướng thơm.", "Đổi vị cho bữa tối.")]));

        result.DishNotes!.Select(note => note.DishId).Should().Equal(2, 3);
        result.DaySummaries.Should().ContainSingle().Which.Should().Be(new AgentDaySummary(2, "Kết luận ngày hai."));
    }

    // TEST-07: A preview made without the model, such as removing a dish, keeps the notes it still has.
    /// <summary>Non-model turns must not wipe the card.</summary>
    [Fact]
    public void Attach_NoModelOutput_KeepsEarlierNotes()
    {
        var source = AgentTestData.Snapshot(Quang, Cha) with { DishNotes = [new(1, "Giới thiệu cũ.", null)], DaySummaries = [new(1, "Kết luận cũ.")] };

        var result = AgentProposalNotes.Attach(AgentTestData.Snapshot(Quang), AgentTestData.Context(source));

        result.DishNotes.Should().ContainSingle().Which.DishId.Should().Be(1);
        result.DaySummaries.Should().BeEmpty();
    }
}
