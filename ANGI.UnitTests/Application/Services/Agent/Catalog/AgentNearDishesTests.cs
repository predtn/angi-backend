using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Services.Agent.Catalog;
using FluentAssertions;

namespace ANGI.UnitTests.Application.Services.Agent.Catalog;

/// <summary>Verifies AgentNearDishes lists dishes sharing words with a missing name, best first, without deciding which mean the same.</summary>
public sealed class AgentNearDishesTests
{
    private static readonly AgentDishText[] Menu =
    [
        new("Cà Phê Nâu Đá", "Cà phê phin pha sữa đặc, đá.", 30000, 1, ["Cà phê"]),
        new("Cà Phê Nâu Đá", "Cà phê phin pha sữa đặc, đá.", 28000, 2, ["Cà phê"]),
        new("Cà Phê Đen Đá", "Robusta pha phin đậm đắng, đá.", 25000, 3, ["Cà phê"]),
        new("Bạc Xỉu", "Sữa tươi, sữa đặc, chút cà phê.", 35000, 4, ["Cà phê"]),
        new("Cá Chẽm Sốt Chanh Dây", "Cá chẽm chiên sốt chanh dây chua ngọt.", 150000, 5, ["Hải sản"]),
        new("Trà Quất Mật Ong", "Trà xanh, quất tươi, mật ong.", 30000, 6, ["Trà"]),
        new("Phở Bò Tái Nạm", "Phở bò Hà Nội.", 50000, 7, ["Phở"])
    ];

    // TEST-01: Name words weigh more than description words; branches merge into one name with the lowest price and their count.
    /// <summary>Ensures "cà phê sữa đá" lists Cà Phê Nâu Đá first, once, with both branches.</summary>
    [Fact]
    public void Find_CoffeeWithMilk_ListsNameMatchesFirst()
    {
        var near = AgentNearDishes.Find("cà phê sữa đá", Menu);

        near.Select(x => x.Name).Should().ContainInOrder("Cà Phê Nâu Đá", "Cà Phê Đen Đá", "Bạc Xỉu");
        near.Should().ContainSingle(x => x.Name == "Cà Phê Nâu Đá").Which.Should().Match<AgentNearDish>(x => x.Price == 28000 && x.Restaurants == 2);
    }

    // TEST-02: An accented query keeps accents, so "cà" never matches "cá"; an unaccented one ignores them.
    /// <summary>Ensures the fish dish stays out of a coffee search and an unaccented search still finds coffee.</summary>
    [Fact]
    public void Find_Accents_DecideHowWordsCompare()
    {
        AgentNearDishes.Find("cà phê sữa đá", Menu).Should().NotContain(x => x.Name.StartsWith("Cá"));
        AgentNearDishes.Find("ca phe sua da", Menu).Select(x => x.Name).Should().Contain("Cà Phê Nâu Đá");
    }

    // TEST-03: One shared name word is enough; nothing in common gives nothing, and a loan word alone matches no Vietnamese name.
    /// <summary>Ensures "trà tắc" reaches Trà Quất Mật Ong through "trà", and "cafe" or "pizza" find nothing.</summary>
    [Fact]
    public void Find_SharedWords_DecideWhatIsListed()
    {
        AgentNearDishes.Find("trà tắc", Menu).Select(x => x.Name).Should().Equal("Trà Quất Mật Ong");
        AgentNearDishes.Find("cafe", Menu).Should().BeEmpty();
        AgentNearDishes.Find("pizza", Menu).Should().BeEmpty();
        // Two words found only in tags still count, so a seafood pizza lists the seafood dish for the model to turn down.
        AgentNearDishes.Find("pizza hải sản", Menu).Select(x => x.Name).Should().Equal("Cá Chẽm Sốt Chanh Dây");
    }

    // TEST-04: A chosen name is compared ignoring case, spacing and Unicode form, never by a part of it.
    /// <summary>Ensures the model's copy of a name matches the catalog name, and a shorter name does not.</summary>
    [Fact]
    public void SameName_ExactNameOnly()
    {
        AgentNearDishes.SameName("Cà Phê Nâu Đá", "  cà phê  nâu đá ").Should().BeTrue();
        AgentNearDishes.SameName("Cà Phê Nâu Đá", "Cà Phê Nâu").Should().BeFalse();
    }
}
