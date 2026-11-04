using ANGI.Application.Common.Interfaces.Repositories.Agent;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Services.Agent.Intake;
using ANGI.Application.Services.Agent.Places;
using FluentAssertions;
using Moq;

namespace ANGI.UnitTests.Application.Services.Agent.Places;

/// <summary>Verifies AgentPlaceResolver locates places from catalog addresses first and trusts a map answer only when its name matches.</summary>
public sealed class AgentPlaceResolverTests
{
    private static readonly string[] Provinces = ["Hà Nội"];

    // TEST-01: The reported itinerary resolves without asking for the province; districts come from addresses, the landmark from its alias, and the far place from the map with a wider radius.
    /// <summary>Ensures the failed test message now yields a place for each day, the map being called only for the name addresses cannot answer.</summary>
    [Fact]
    public async Task ReadAsync_ReportedItinerary_ResolvesEachDay()
    {
        var search = new Mock<IPlaceSearchService>();
        search.Setup(x => x.SearchAsync("Hòa Lạc, Hà Nội", It.IsAny<decimal?>(), It.IsAny<decimal?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new AgentPlaceCandidate("Hoa Lac", "Hà Nội, Việt Nam", "locality", 21.0445m, 105.50493m)]);

        var trip = await Resolver(search.Object).ReadAsync(Context("Ngày 1 tôi ở Hoàn kiếm\nNgày 2 tôi đi lăng bác\nNgày 3 tôi ở Hòa Lạc\nHãy lập lịch đi ăn"), Provinces, default);

        trip!.Province.Should().Be("Hà Nội");
        trip.Unresolved.Should().BeEmpty();
        trip.LastDay.Should().Be(3);
        trip.DayAreas.Select(area => area.Label).Should().Equal("Hoàn Kiếm", "Lăng Bác", "Hòa Lạc");
        trip.DayAreas[0].Latitude.Should().Be(21.03m);
        trip.DayAreas[0].RadiusKm.Should().Be(3m);
        trip.DayAreas[1].Latitude.Should().Be(21.0368m, "the landmark uses its own position, not the middle of its district");
        trip.DayAreas[1].Longitude.Should().Be(105.8347m);
        trip.DayAreas[2].RadiusKm.Should().BeGreaterThan(AgentItineraryPolicy.WideRadiusKm);
        search.Verify(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<decimal?>(), It.IsAny<decimal?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-02: A map answer that only looks similar is refused; covers the real "Lăng Bác" style mismatch, the city returned for nonsense, no result and an unavailable service.
    /// <summary>Ensures a confident wrong map answer never moves a day to the wrong place; the place is reported instead.</summary>
    [Theory]
    [InlineData("poi", "Làng cổ Times Biên City")]
    [InlineData("place", "Times City")]
    [InlineData("poi", null)]
    public async Task ReadAsync_MapAnswerNotMatchingTheName_IsUnresolved(string type, string? name)
    {
        var search = new Mock<IPlaceSearchService>();
        search.Setup(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<decimal?>(), It.IsAny<decimal?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(name is null ? [] : [new AgentPlaceCandidate(name, "Hà Nội", type, 21.03m, 105.85m)]);

        var trip = await Resolver(search.Object).ReadAsync(Context("Ngày 1 tôi đi Times City", "Hà Nội"), Provinces, default);

        trip!.DayAreas.Should().BeEmpty();
        trip.Unresolved.Should().Equal("Times City");
    }

    // TEST-03: Without a map service a landmark is reported, not thrown; catalog districts still work.
    /// <summary>Ensures the feature degrades to catalog addresses when Mapbox is not configured.</summary>
    [Fact]
    public async Task ReadAsync_NoMapService_StillUsesAddresses()
    {
        var trip = await Resolver(null).ReadAsync(Context("Ngày 1 ở Cầu Giấy, ngày 2 đi Times City"), Provinces, default);

        trip!.DayAreas.Single().Label.Should().Be("Cầu Giấy");
        trip.Unresolved.Should().Equal("Times City");
        trip.LastDay.Should().Be(2, "a day whose place is unknown still belongs to the trip");
    }

    // TEST-04: A correctly named place with no restaurant within the largest radius is unresolved, whatever the map says.
    /// <summary>Ensures a place too far from any restaurant never produces an empty day.</summary>
    [Fact]
    public async Task ReadAsync_NoRestaurantInLargestRadius_IsUnresolved()
    {
        var search = new Mock<IPlaceSearchService>();
        search.Setup(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<decimal?>(), It.IsAny<decimal?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new AgentPlaceCandidate("Phú Quốc", null, "locality", 10.2m, 103.96m)]);

        var trip = await Resolver(search.Object).ReadAsync(Context("Ngày 1 tôi đi Phú Quốc", "Hà Nội"), Provinces, default);

        trip!.DayAreas.Should().BeEmpty();
        trip.Unresolved.Should().Equal("Phú Quốc");
    }

    // TEST-05: A bare unknown phrase is left to the normal flow without a map request or any catalog read of "Unresolved".
    /// <summary>Ensures "ngày 1 abcde xyz" costs no map request and is not announced as a place.</summary>
    [Fact]
    public async Task ReadAsync_BareUnknownPhrase_ReturnsNullWithoutMapRequest()
    {
        var search = new Mock<IPlaceSearchService>(MockBehavior.Strict);

        (await Resolver(search.Object).ReadAsync(Context("ngày 1 abcde xyz"), Provinces, default)).Should().BeNull();
    }

    // TEST-06: Messages with no place never read the catalog, so ordinary chat turns stay as cheap as before.
    /// <summary>Ensures the catalog query only runs when a place was actually named.</summary>
    [Theory]
    [InlineData("lập lịch cho tôi")][InlineData("xin chào")][InlineData("ngày 1 ăn hải sản")]
    public async Task ReadAsync_NoPlaceNamed_DoesNotReadCatalog(string content)
    {
        var catalog = new Mock<IAgentCatalogRepository>(MockBehavior.Strict);

        (await new AgentPlaceResolver(catalog.Object).ReadAsync(Context(content), Provinces, default)).Should().BeNull();
    }

    // TEST-07: Naming the province means the whole province: a trip answer stays province-wide, and a day is cleared rather than bound to the city centre.
    /// <summary>Ensures "ở Hà Nội" keeps meaning the whole city.</summary>
    [Fact]
    public async Task ReadAsync_ProvinceNamed_MeansWholeProvince()
    {
        (await Resolver(null).ReadAsync(Context("lập lịch ở Hà Nội"), Provinces, default)).Should().BeNull();

        var trip = await Resolver(null).ReadAsync(Context("Ngày 1 ở Hà Nội, ngày 2 ở Cầu Giấy"), Provinces, default);

        trip!.WholeProvinceDays.Should().Equal((short)1);
        trip.DayAreas.Single().DayNumber.Should().Be(2);
        trip.Province.Should().Be("Hà Nội");
    }

    // TEST-08: One area for the whole trip resolves when the place question is open or the message asks for a plan; a passing mention elsewhere does not.
    /// <summary>Ensures "Vậy thì ở Cầu Giấy" answers the question while "hôm nay trời đẹp ở Cầu Giấy" stays with the normal flow.</summary>
    [Fact]
    public async Task ReadAsync_TripArea_OnlyWhenPlanningOrAsked()
    {
        var asked = Context("Hai bà trưng") ;
        asked.State = asked.State with { PlanningIntake = AgentIntakePolicy.Details };

        var answer = await Resolver(null).ReadAsync(asked, Provinces, default);

        answer!.TripArea!.Label.Should().Be("Hai Bà Trưng");
        answer.TripArea.Province.Should().Be("Hà Nội");
        (await Resolver(null).ReadAsync(Context("lập lịch 3 ngày ở Cầu Giấy"), Provinces, default))!.TripArea!.RadiusKm.Should().Be(3m);
        (await Resolver(null).ReadAsync(Context("hôm nay trời đẹp ở Cầu Giấy"), Provinces, default)).Should().BeNull();
    }

    // TEST-09: The radius is the smallest step holding enough restaurants; a thin area takes a wider step than a dense one.
    /// <summary>Ensures a thin district still has enough restaurants for every meal.</summary>
    [Fact]
    public async Task ReadAsync_ThinArea_TakesWiderRadius()
    {
        var trip = await Resolver(null).ReadAsync(Context("Ngày 1 tôi ở Hoàng Mai", "Hà Nội"), Provinces, default);

        // Hoàng Mai chỉ có 5 quán, cách Hoàn Kiếm khoảng 6 km nên 8 km mới gom đủ.
        trip!.DayAreas.Single().RadiusKm.Should().Be(8m);
    }

    // TEST-10: With no plan yet, a message that only names a place is one area for the whole trip; a name that could be a dish is checked against addresses only.
    /// <summary>Ensures "ở Hoàn Kiếm" and "đi Lăng Bác" start a plan around that place, and "đổi sang Times City" never asks the map.</summary>
    [Fact]
    public async Task ReadAsync_BareAreaWithoutPlan_IsTripArea()
    {
        var search = new Mock<IPlaceSearchService>(MockBehavior.Strict);

        var district = await Resolver(search.Object).ReadAsync(Context("ở Hoàn Kiếm"), Provinces, default);
        var landmark = await Resolver(search.Object).ReadAsync(Context("tôi đi Lăng Bác"), Provinces, default);

        district!.TripArea!.Label.Should().Be("Hoàn Kiếm");
        district.TripArea.Latitude.Should().Be(21.03m);
        landmark!.TripArea!.Label.Should().Be("Lăng Bác");
        landmark.TripArea.Latitude.Should().Be(21.0368m);
        (await Resolver(search.Object).ReadAsync(Context("đổi sang Times City"), Provinces, default)).Should().BeNull();
    }

    // TEST-11: Locating one area leaves out the whole province, and the map is used only when the caller allows it.
    /// <summary>Ensures "đổi sang khu Hà Nội" is not treated as a smaller area inside the province.</summary>
    [Fact]
    public async Task LocateAsync_ProvinceOrUnsearchedName_ReturnsNull()
    {
        var search = new Mock<IPlaceSearchService>(MockBehavior.Strict);

        (await Resolver(search.Object).LocateAsync("Hà Nội", "Hà Nội", true, Provinces, default)).Should().BeNull();
        (await Resolver(search.Object).LocateAsync("Times City", "Hà Nội", false, Provinces, default)).Should().BeNull();
        (await Resolver(search.Object).LocateAsync("Cầu Giấy", "Hà Nội", false, Provinces, default))!.Label.Should().Be("Cầu Giấy");
    }

    // TEST-12: Other provinces and well-known destinations are never located inside the trip's province, even though the map offers a business with that name; covers accents, no accents and a "thành phố" prefix.
    /// <summary>Reproduces "Đà Nẵng, Hà Nội" returning a Hà Nội place, which would have planned the day in the wrong city.</summary>
    [Theory]
    [InlineData("Ngày 1 tôi đi Đà Nẵng", "Đà Nẵng")]
    [InlineData("ngay 1 toi di da nang", "da nang")]
    [InlineData("Ngày 1 đi Phú Quốc", "Phú Quốc")]
    [InlineData("Ngày 1 đi Huế", "Huế")]
    [InlineData("Ngày 1 đi thành phố Hồ Chí Minh", "thành phố Hồ Chí Minh")]
    public async Task ReadAsync_PlaceOutsideCatalog_IsUnresolvedWithoutMapRequest(string content, string place)
    {
        var search = new Mock<IPlaceSearchService>(MockBehavior.Strict);

        var trip = await Resolver(search.Object).ReadAsync(Context(content, "Hà Nội"), Provinces, default);

        trip!.DayAreas.Should().BeEmpty();
        trip.Unresolved.Should().Equal(place);
    }

    // TEST-13: A street named after another city stays a street here, with or without its name in an address; the map still answers when addresses do not.
    /// <summary>Ensures blocking "Huế" does not block "phố Huế", a real Hà Nội street.</summary>
    [Fact]
    public async Task ReadAsync_StreetNamedAfterCity_IsStillAPlace()
    {
        var search = new Mock<IPlaceSearchService>();
        search.Setup(x => x.SearchAsync("phố Huế, Hà Nội", It.IsAny<decimal?>(), It.IsAny<decimal?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new AgentPlaceCandidate("Phố Huế", "Hai Bà Trưng, Hà Nội", "street", 21.015m, 105.851m)]);

        var mapped = await Resolver(search.Object).ReadAsync(Context("Ngày 1 tôi đi phố Huế", "Hà Nội"), Provinces, default);
        var addressed = await Resolver(null).ReadAsync(Context("Ngày 1 tôi đi phố Hàng Bạc", "Hà Nội"), Provinces, default);
        var bareStreet = await Resolver(null).ReadAsync(Context("Ngày 1 tôi đi đường Xuân Thủy", "Hà Nội"), Provinces, default);

        mapped!.DayAreas.Single().Label.Should().Be("Phố Huế");
        addressed!.DayAreas.Single().Latitude.Should().Be(21.03m);
        bareStreet!.DayAreas.Single().Longitude.Should().Be(105.79m);
    }

    // TEST-14: A place only the map knows resolves whatever its position in the itinerary and without a stated province; covers it first, and alone.
    /// <summary>Reproduces "Ngày 1 đi Hòa Lạc, ngày 2 đi Văn Miếu", where the first stop was searched across the whole country and lost.</summary>
    [Theory]
    [InlineData("Ngày 1 đi Hòa Lạc, ngày 2 ở Cầu Giấy", 2)]
    [InlineData("Ngày 1 đi Hòa Lạc", 1)]
    public async Task ReadAsync_MapPlaceFirst_IsSearchedInCatalogProvince(string content, int areas)
    {
        var search = new Mock<IPlaceSearchService>();
        search.Setup(x => x.SearchAsync("Hòa Lạc, Hà Nội", It.IsAny<decimal?>(), It.IsAny<decimal?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new AgentPlaceCandidate("Hoa Lac", "Hà Nội, Việt Nam", "locality", 21.0445m, 105.50493m)]);

        var trip = await Resolver(search.Object).ReadAsync(Context(content), Provinces, default);

        trip!.DayAreas.Should().HaveCount(areas);
        trip.DayAreas[0].Label.Should().Be("Hòa Lạc");
        trip.Province.Should().Be("Hà Nội");
        trip.Unresolved.Should().BeEmpty();
    }

    // TEST-15: A business that only carries the phrase in its name proves nothing; covers one business, scattered businesses, and a phrase found only in an address.
    /// <summary>Reproduces "Hồ Gươm Plaza" in Hà Đông and "1001 tủ đồ" read as "Tự Do": the place is reported instead of moving the day to the wrong district.</summary>
    [Theory]
    [InlineData("one-business")]
    [InlineData("scattered")]
    [InlineData("address-only")]
    public async Task ReadAsync_WeakMapEvidence_IsUnresolved(string area)
    {
        AgentPlaceCandidate Business(string name, decimal latitude, string? address = null) => new(name, address, "poi", latitude, 105.80m);
        AgentPlaceCandidate[] candidates = area switch
        {
            "one-business" => [Business("Thủy Tiên Plaza", 20.98m)],
            "scattered" => [Business("Thủy Tiên Plaza", 20.98m), Business("Cafe Thủy Tiên", 21.03m), Business("Khách sạn Thủy Tiên", 21.08m)],
            _ => [Business("Garage Sale", 21.03m, "12 Thủy Tiên, Hà Nội")]
        };
        var search = new Mock<IPlaceSearchService>();
        search.Setup(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<decimal?>(), It.IsAny<decimal?>(), It.IsAny<CancellationToken>())).ReturnsAsync(candidates);

        var trip = await Resolver(search.Object).ReadAsync(Context("Ngày 1 tôi đi Thủy Tiên", "Hà Nội"), Provinces, default);

        trip!.DayAreas.Should().BeEmpty();
        trip.Unresolved.Should().Equal("Thủy Tiên");
    }

    // TEST-16: Strong map evidence is used; covers an area type, a business named exactly that, and most businesses sitting together, whose middle becomes the place.
    /// <summary>Keeps "Times City", "Đại học Bách Khoa" and "Hòa Lạc" working under the stricter rule.</summary>
    [Theory]
    [InlineData("area", 21.0)]
    [InlineData("exact", 21.0)]
    [InlineData("cluster", 21.001)]
    public async Task ReadAsync_StrongMapEvidence_IsUsed(string area, double latitude)
    {
        AgentPlaceCandidate Business(string name, decimal lat) => new(name, null, "poi", lat, 105.86m);
        AgentPlaceCandidate[] candidates = area switch
        {
            "area" => [new("Thuy Tien", "Hà Nội", "locality", 21.0m, 105.86m)],
            "exact" => [Business("Thủy Tiên Hà Nội", 21.0m)],
            _ => [Business("Shop Thủy Tiên", 21.0m), Business("Spa Thủy Tiên", 21.001m), Business("Căn hộ Thủy Tiên", 21.002m), Business("Taxi Thủy Tiên", 20.7m)]
        };
        var search = new Mock<IPlaceSearchService>();
        search.Setup(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<decimal?>(), It.IsAny<decimal?>(), It.IsAny<CancellationToken>())).ReturnsAsync(candidates);

        var trip = await Resolver(search.Object).ReadAsync(Context("Ngày 1 tôi đi Thủy Tiên", "Hà Nội"), Provinces, default);

        trip!.DayAreas.Single().Latitude.Should().Be((decimal)latitude);
    }

    // TEST-17: Known landmarks and a free day never reach the map, with or without accents.
    /// <summary>Ensures Hồ Gươm is at the lake however the map answers, and "đi tự do" means the whole province.</summary>
    [Theory]
    [InlineData("Ngày 1 đi Hồ Gươm", 21.0287)]
    [InlineData("ngay 1 di ho guom", 21.0287)]
    [InlineData("Ngày 1 ở Hồ Hoàn Kiếm", 21.0287)]
    [InlineData("Ngày 1 đi Phố Cổ", 21.0341)]
    public async Task ReadAsync_KnownLandmark_UsesItsPosition(string content, double latitude)
    {
        var search = new Mock<IPlaceSearchService>(MockBehavior.Strict);

        var trip = await Resolver(search.Object).ReadAsync(Context(content, "Hà Nội"), Provinces, default);

        trip!.DayAreas.Single().Latitude.Should().Be((decimal)latitude);
    }

    // TEST-18: A free day is the whole province and still counts as a day of the trip; covers several wordings.
    /// <summary>Replays "Ngày 3 : Tôi đi tự do", which used to bind the day to an unrelated point.</summary>
    [Theory]
    [InlineData("Ngày 1 ở Cầu Giấy, ngày 2 tôi đi tự do")]
    [InlineData("Ngày 1 ở Cầu Giấy, ngày 2 đi chơi tự do")]
    [InlineData("Ngày 1 ở Cầu Giấy, ngày 2 chưa biết")]
    [InlineData("Ngày 1 ở Cầu Giấy, ngày 2: tùy ý")]
    public async Task ReadAsync_FreeDay_IsWholeProvince(string content)
    {
        var search = new Mock<IPlaceSearchService>(MockBehavior.Strict);

        var trip = await Resolver(search.Object).ReadAsync(Context(content, "Hà Nội"), Provinces, default);

        trip!.WholeProvinceDays.Should().Equal((short)2);
        trip.DayAreas.Single().DayNumber.Should().Be(1);
        trip.Unresolved.Should().BeEmpty();
        trip.LastDay.Should().Be(2);
    }

    // TEST-19: The district of the nearest restaurant is reported so a wrong location shows; it is left out when it repeats the name or the search is wide.
    /// <summary>Ensures "Hồ Gươm (khu Hà Đông)" would have been visible before any dish was chosen.</summary>
    [Fact]
    public async Task ReadAsync_District_ShownOnlyWhenItAddsSomething()
    {
        var landmark = await Resolver(null).ReadAsync(Context("Ngày 1 đi Lăng Bác", "Hà Nội"), Provinces, default);
        var district = await Resolver(null).ReadAsync(Context("Ngày 1 ở Hoàn Kiếm", "Hà Nội"), Provinces, default);

        landmark!.DayAreas.Single().District.Should().Be("Ba Đình");
        district!.DayAreas.Single().District.Should().BeNull();
    }

    /// <summary>Builds a catalog of dense Hoàn Kiếm, Ba Đình and Cầu Giấy areas and a thin Hoàng Mai, with addresses in the real format.</summary>
    private static AgentPlaceResolver Resolver(IPlaceSearchService? search)
    {
        var places = Enumerable.Range(0, 25).Select(_ => new AgentCatalogPlace("Hà Nội", "12 Phố Hàng Bạc, P. Hàng Đào, Hoàn Kiếm, Hà Nội", 21.03m, 105.85m))
            .Concat(Enumerable.Range(0, 25).Select(_ => new AgentCatalogPlace("Hà Nội", "5 Đội Cấn, P. Cống Vị, Ba Đình, Hà Nội", 21.04m, 105.83m)))
            .Concat(Enumerable.Range(0, 25).Select(_ => new AgentCatalogPlace("Hà Nội", "9 Xuân Thủy, P. Dịch Vọng, Cầu Giấy, Hà Nội", 21.03m, 105.79m)))
            .Concat(Enumerable.Range(0, 5).Select(_ => new AgentCatalogPlace("Hà Nội", "7 Giải Phóng, P. Giáp Bát, Hoàng Mai, Hà Nội", 20.98m, 105.84m)))
            .Append(new AgentCatalogPlace("Hà Nội", "1 Phố Hai Bà Trưng, Hoàn Kiếm, Hà Nội", 21.025m, 105.85m)).ToArray();
        var catalog = new Mock<IAgentCatalogRepository>();
        catalog.Setup(x => x.GetPublicPlacesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(places);
        return new(catalog.Object, search);
    }

    /// <summary>Builds a turn with the given message and optional known province.</summary>
    private static AgentContext Context(string content, string? province = null)
    {
        var context = AgentTestData.Context();
        context.State = new AgentState { Province = province };
        context.Request.Content = content;
        return context;
    }
}
