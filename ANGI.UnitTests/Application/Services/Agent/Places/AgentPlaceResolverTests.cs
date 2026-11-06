using ANGI.Application.Common.Interfaces.Repositories.Agent;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Services.Agent.Intake;
using ANGI.Application.Services.Agent;
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
    public async Task LocateAsync_ReportedItinerary_ResolvesEachDay()
    {
        // STEP-01: Mock IPlaceSearchService; stub search.SearchAsync.
        var search = new Mock<IPlaceSearchService>();
        search.Setup(x => x.SearchAsync("Hòa Lạc, Hà Nội", It.IsAny<decimal?>(), It.IsAny<decimal?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new AgentPlaceCandidate("Hoa Lac", "Hà Nội, Việt Nam", "locality", 21.0445m, 105.50493m)]);

        // STEP-02: Locate the stops; assert trip.Province is the expected text, trip.Unresolved is empty, trip.LastDay is 3 and 6 more.
        var trip = await Locate(Resolver(search.Object), null, (1, "Hoàn kiếm"), (2, "lăng bác"), (3, "Hòa Lạc"));

        trip!.Province.Should().Be("Hà Nội");
        trip.Unresolved.Should().BeEmpty();
        trip.LastDay.Should().Be(3);
        trip.DayAreas.Select(area => area.Label).Should().Equal("Hoàn Kiếm", "Lăng Bác", "Hòa Lạc");
        trip.DayAreas[0].Latitude.Should().Be(21.03m);
        trip.DayAreas[0].RadiusKm.Should().Be(3m);
        trip.DayAreas[1].Latitude.Should().Be(21.0368m, "the landmark uses its own position, not the middle of its district");
        trip.DayAreas[1].Longitude.Should().Be(105.8347m);
        trip.DayAreas[2].RadiusKm.Should().BeGreaterThan(AgentPlaceResolver.WideRadiusKm);
        // STEP-03: Verify SearchAsync is called once.
        search.Verify(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<decimal?>(), It.IsAny<decimal?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-02: A map answer that only looks similar is refused; covers the real "Lăng Bác" style mismatch, the city returned for nonsense, no result and an unavailable service.
    /// <summary>Ensures a confident wrong map answer never moves a day to the wrong place; the place is reported instead.</summary>
    [Theory]
    [InlineData("poi", "Làng cổ Times Biên City")]
    [InlineData("place", "Times City")]
    [InlineData("poi", null)]
    public async Task LocateAsync_MapAnswerNotMatchingTheName_IsUnresolved(string type, string? name)
    {
        // STEP-01: Mock IPlaceSearchService; stub search.SearchAsync.
        var search = new Mock<IPlaceSearchService>();
        search.Setup(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<decimal?>(), It.IsAny<decimal?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(name is null ? [] : [new AgentPlaceCandidate(name, "Hà Nội", type, 21.03m, 105.85m)]);

        // STEP-02: Locate the stops; assert trip.DayAreas is empty and trip.Unresolved equals "Times City".
        var trip = await Locate(Resolver(search.Object), "Hà Nội", (1, "Times City"));

        trip!.DayAreas.Should().BeEmpty();
        trip.Unresolved.Should().Equal("Times City");
    }

    // TEST-03: Without a map service a landmark is reported, not thrown; catalog districts still work.
    /// <summary>Ensures the feature degrades to catalog addresses when Mapbox is not configured.</summary>
    [Fact]
    public async Task LocateAsync_NoMapService_StillUsesAddresses()
    {
        // STEP-01: Locate the stops; assert trip.DayAreas.Single().Label is the expected text, trip.Unresolved equals "Times City" and trip.LastDay is 2.
        var trip = await Locate(Resolver(null), null, (1, "Cầu Giấy"), (2, "Times City"));

        trip!.DayAreas.Single().Label.Should().Be("Cầu Giấy");
        trip.Unresolved.Should().Equal("Times City");
        trip.LastDay.Should().Be(2, "a day whose place is unknown still belongs to the trip");
    }

    // TEST-04: A correctly named place with no restaurant within the largest radius is unresolved, whatever the map says.
    /// <summary>Ensures a place too far from any restaurant never produces an empty day.</summary>
    [Fact]
    public async Task LocateAsync_NoRestaurantInLargestRadius_IsUnresolved()
    {
        // STEP-01: Mock IPlaceSearchService; stub search.SearchAsync.
        var search = new Mock<IPlaceSearchService>();
        search.Setup(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<decimal?>(), It.IsAny<decimal?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new AgentPlaceCandidate("Phú Quốc", null, "locality", 10.2m, 103.96m)]);

        // STEP-02: Locate the stops; assert trip.DayAreas is empty and trip.Unresolved equals the expected text.
        var trip = await Locate(Resolver(search.Object), "Hà Nội", (1, "Phú Quốc"));

        trip!.DayAreas.Should().BeEmpty();
        trip.Unresolved.Should().Equal("Phú Quốc");
    }

    // TEST-05: Naming the province means the whole province: a trip answer stays province-wide, and a day is cleared rather than bound to the city centre.
    /// <summary>Ensures "ở Hà Nội" keeps meaning the whole city.</summary>
    [Fact]
    public async Task LocateAsync_ProvinceNamed_MeansWholeProvince()
    {
        // STEP-01: Locate the stops; assert Resolver().ReadAsync() is null, trip.WholeProvinceDays equals 1, trip.DayAreas.Single().DayNumber is 2 and 1 more.
        var trip = await Locate(Resolver(null), null, (1, "Hà Nội"), (2, "Cầu Giấy"));

        trip!.WholeProvinceDays.Should().Equal((short)1);
        trip.DayAreas.Single().DayNumber.Should().Be(2);
        trip.Province.Should().Be("Hà Nội");
    }

    // TEST-06: One area named for the whole trip is located; the province itself means no area, and a name nowhere on ANGI comes back as unresolved.
    /// <summary>The model read the place; the backend still owns where it is.</summary>
    [Fact]
    public async Task LocateAsync_TripArea_LocatesOrReportsIt()
    {
        // STEP-01: Locate an area, the province and an unknown name.
        var area = await Resolver(null).LocateAsync("Hai bà trưng", [], new AgentState(), Provinces, default);
        var province = await Resolver(null).LocateAsync("Hà Nội", [], new AgentState(), Provinces, default);
        var unknown = await Resolver(null).LocateAsync("abcde xyz", [], new AgentState { Province = "Hà Nội" }, Provinces, default);

        // STEP-02: Assert what each one found.
        area.TripArea!.Label.Should().Be("Hai Bà Trưng");
        area.TripArea.Province.Should().Be("Hà Nội");
        province.TripArea.Should().BeNull();
        province.Province.Should().Be("Hà Nội");
        unknown.TripArea.Should().BeNull();
        unknown.Unresolved.Should().Equal("abcde xyz");
        unknown.Province.Should().Be("Hà Nội");
    }

    // TEST-07: The radius is the smallest step holding enough restaurants; a thin area takes a wider step than a dense one.
    /// <summary>Ensures a thin district still has enough restaurants for every meal.</summary>
    [Fact]
    public async Task LocateAsync_ThinArea_TakesWiderRadius()
    {
        // STEP-01: Locate the stops; assert trip.DayAreas.Single().RadiusKm is 8m.
        var trip = await Locate(Resolver(null), "Hà Nội", (1, "Hoàng Mai"));

        // Hoàng Mai chỉ có 5 quán, cách Hoàn Kiếm khoảng 6 km nên 8 km mới gom đủ.
        trip!.DayAreas.Single().RadiusKm.Should().Be(8m);
    }

    // TEST-08: Stops the model read are located day by day; a stop with no place is a day with no fixed place, the province comes from the first place known, and a place the map cannot confirm is unresolved.
    /// <summary>Replays "ngày 1 Hồ Gươm, ngày 2 Lăng Bác, ngày 3 tự do" as the model passes it, with no map request for known landmarks.</summary>
    [Fact]
    public async Task LocateAsync_Stops_LocatesEachDay()
    {
        // STEP-01: Mock IPlaceSearchService with no results.
        var search = new Mock<IPlaceSearchService>();
        search.Setup(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<decimal?>(), It.IsAny<decimal?>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        AgentLocateStop[] stops = [new([1], "Hồ Gươm"), new([2], "Lăng Bác"), new([3], null), new([4], "abcde xyz")];

        // STEP-02: Locate the stops.
        var trip = await Resolver(search.Object).LocateAsync(null, stops, new AgentState(), Provinces, default);

        // STEP-03: Assert the days, the open day, the province and the unresolved place.
        trip.DayAreas.Select(area => (area.DayNumber, area.Label)).Should().Equal(((short)1, "Hồ Gươm"), ((short)2, "Lăng Bác"));
        trip.DayAreas[0].Latitude.Should().Be(21.0287m);
        trip.WholeProvinceDays.Should().Equal((short)3);
        trip.Unresolved.Should().Equal("abcde xyz");
        trip.Province.Should().Be("Hà Nội");
        trip.LastDay.Should().Be(4);
    }

    // TEST-09: Locating one area leaves out the whole province, and the map is used only when the caller allows it.
    /// <summary>Ensures "đổi sang khu Hà Nội" is not treated as a smaller area inside the province.</summary>
    [Fact]
    public async Task LocateAsync_ProvinceOrUnsearchedName_ReturnsNull()
    {
        // STEP-01: Mock IPlaceSearchService.
        var search = new Mock<IPlaceSearchService>(MockBehavior.Strict);

        // STEP-02: Assert Resolver().LocateAsync() is null and Resolver().LocateAsync().Label is the expected text.
        (await Resolver(search.Object).LocateAsync("Hà Nội", "Hà Nội", true, Provinces, default)).Should().BeNull();
        (await Resolver(search.Object).LocateAsync("Times City", "Hà Nội", false, Provinces, default)).Should().BeNull();
        (await Resolver(search.Object).LocateAsync("Cầu Giấy", "Hà Nội", false, Provinces, default))!.Label.Should().Be("Cầu Giấy");
    }

    // TEST-10: Other provinces and well-known destinations are never located inside the trip's province, even though the map offers a business with that name; covers accents, no accents and a "thành phố" prefix.
    /// <summary>Reproduces "Đà Nẵng, Hà Nội" returning a Hà Nội place, which would have planned the day in the wrong city.</summary>
    [Theory]
    [InlineData("Đà Nẵng")]
    [InlineData("da nang")]
    [InlineData("Phú Quốc")]
    [InlineData("Huế")]
    [InlineData("thành phố Hồ Chí Minh")]
    public async Task LocateAsync_PlaceOutsideCatalog_IsUnresolvedWithoutMapRequest(string place)
    {
        // STEP-01: Mock IPlaceSearchService.
        var search = new Mock<IPlaceSearchService>(MockBehavior.Strict);

        // STEP-02: Locate the stops; assert trip.DayAreas is empty and trip.Unresolved equals place.
        var trip = await Locate(Resolver(search.Object), "Hà Nội", (1, place));

        trip!.DayAreas.Should().BeEmpty();
        trip.Unresolved.Should().Equal(place);
    }

    // TEST-11: A street named after another city stays a street here, with or without its name in an address; the map still answers when addresses do not.
    /// <summary>Ensures blocking "Huế" does not block "phố Huế", a real Hà Nội street.</summary>
    [Fact]
    public async Task LocateAsync_StreetNamedAfterCity_IsStillAPlace()
    {
        // STEP-01: Mock IPlaceSearchService; stub search.SearchAsync.
        var search = new Mock<IPlaceSearchService>();
        search.Setup(x => x.SearchAsync("phố Huế, Hà Nội", It.IsAny<decimal?>(), It.IsAny<decimal?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new AgentPlaceCandidate("Phố Huế", "Hai Bà Trưng, Hà Nội", "street", 21.015m, 105.851m)]);

        // STEP-02: Read mapped from the response and 2 more; assert mapped.DayAreas.Single().Label is the expected text and 2 more.
        var mapped = await Locate(Resolver(search.Object), "Hà Nội", (1, "phố Huế"));
        var addressed = await Locate(Resolver(null), "Hà Nội", (1, "phố Hàng Bạc"));
        var bareStreet = await Locate(Resolver(null), "Hà Nội", (1, "đường Xuân Thủy"));

        mapped!.DayAreas.Single().Label.Should().Be("Phố Huế");
        addressed!.DayAreas.Single().Latitude.Should().Be(21.03m);
        bareStreet!.DayAreas.Single().Longitude.Should().Be(105.79m);
    }

    // TEST-12: A place only the map knows resolves whatever its position in the itinerary and without a stated province; covers it first, and alone.
    /// <summary>Reproduces "Ngày 1 đi Hòa Lạc, ngày 2 đi Văn Miếu", where the first stop was searched across the whole country and lost.</summary>
    [Theory]
    [InlineData(true, 2)]
    [InlineData(false, 1)]
    public async Task LocateAsync_MapPlaceFirst_IsSearchedInCatalogProvince(bool secondStop, int areas)
    {
        // STEP-01: Mock IPlaceSearchService; stub search.SearchAsync.
        var search = new Mock<IPlaceSearchService>();
        search.Setup(x => x.SearchAsync("Hòa Lạc, Hà Nội", It.IsAny<decimal?>(), It.IsAny<decimal?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new AgentPlaceCandidate("Hoa Lac", "Hà Nội, Việt Nam", "locality", 21.0445m, 105.50493m)]);

        // STEP-02: Locate the stops; assert trip.DayAreas has areas item(s), trip.DayAreas[0].Label is the expected text and 2 more.
        var trip = secondStop ? await Locate(Resolver(search.Object), null, (1, "Hòa Lạc"), (2, "Cầu Giấy")) : await Locate(Resolver(search.Object), null, (1, "Hòa Lạc"));

        trip!.DayAreas.Should().HaveCount(areas);
        trip.DayAreas[0].Label.Should().Be("Hòa Lạc");
        trip.Province.Should().Be("Hà Nội");
        trip.Unresolved.Should().BeEmpty();
    }

    // TEST-13: A business that only carries the phrase in its name proves nothing; covers one business, scattered businesses, and a phrase found only in an address.
    /// <summary>Reproduces "Hồ Gươm Plaza" in Hà Đông and "1001 tủ đồ" read as "Tự Do": the place is reported instead of moving the day to the wrong district.</summary>
    [Theory]
    [InlineData("one-business")]
    [InlineData("scattered")]
    [InlineData("address-only")]
    public async Task LocateAsync_WeakMapEvidence_IsUnresolved(string area)
    {
        // STEP-01: Define helper Business and derive candidates from the expected value.
        AgentPlaceCandidate Business(string name, decimal latitude, string? address = null) => new(name, address, "poi", latitude, 105.80m);
        AgentPlaceCandidate[] candidates = area switch
        {
            "one-business" => [Business("Thủy Tiên Plaza", 20.98m)],
            "scattered" => [Business("Thủy Tiên Plaza", 20.98m), Business("Cafe Thủy Tiên", 21.03m), Business("Khách sạn Thủy Tiên", 21.08m)],
            _ => [Business("Garage Sale", 21.03m, "12 Thủy Tiên, Hà Nội")]
        };
        // STEP-02: Mock IPlaceSearchService; stub search.SearchAsync.
        var search = new Mock<IPlaceSearchService>();
        search.Setup(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<decimal?>(), It.IsAny<decimal?>(), It.IsAny<CancellationToken>())).ReturnsAsync(candidates);

        // STEP-03: Locate the stops; assert trip.DayAreas is empty and trip.Unresolved equals the expected text.
        var trip = await Locate(Resolver(search.Object), "Hà Nội", (1, "Thủy Tiên"));

        trip!.DayAreas.Should().BeEmpty();
        trip.Unresolved.Should().Equal("Thủy Tiên");
    }

    // TEST-14: Strong map evidence is used; covers an area type, a business named exactly that, and most businesses sitting together, whose middle becomes the place.
    /// <summary>Keeps "Times City", "Đại học Bách Khoa" and "Hòa Lạc" working under the stricter rule.</summary>
    [Theory]
    [InlineData("area", 21.0)]
    [InlineData("exact", 21.0)]
    [InlineData("cluster", 21.001)]
    public async Task LocateAsync_StrongMapEvidence_IsUsed(string area, double latitude)
    {
        // STEP-01: Define helper Business and derive candidates from the expected value.
        AgentPlaceCandidate Business(string name, decimal lat) => new(name, null, "poi", lat, 105.86m);
        AgentPlaceCandidate[] candidates = area switch
        {
            "area" => [new("Thuy Tien", "Hà Nội", "locality", 21.0m, 105.86m)],
            "exact" => [Business("Thủy Tiên Hà Nội", 21.0m)],
            _ => [Business("Shop Thủy Tiên", 21.0m), Business("Spa Thủy Tiên", 21.001m), Business("Căn hộ Thủy Tiên", 21.002m), Business("Taxi Thủy Tiên", 20.7m)]
        };
        // STEP-02: Mock IPlaceSearchService; stub search.SearchAsync.
        var search = new Mock<IPlaceSearchService>();
        search.Setup(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<decimal?>(), It.IsAny<decimal?>(), It.IsAny<CancellationToken>())).ReturnsAsync(candidates);

        // STEP-03: Locate the stops; assert trip.DayAreas.Single().Latitude is latitude.
        var trip = await Locate(Resolver(search.Object), "Hà Nội", (1, "Thủy Tiên"));

        trip!.DayAreas.Single().Latitude.Should().Be((decimal)latitude);
    }

    // TEST-15: Known landmarks and a free day never reach the map, with or without accents.
    /// <summary>Ensures Hồ Gươm is at the lake however the map answers, and "đi tự do" means the whole province.</summary>
    [Theory]
    [InlineData("Hồ Gươm", 21.0287)]
    [InlineData("ho guom", 21.0287)]
    [InlineData("Hồ Hoàn Kiếm", 21.0287)]
    [InlineData("Phố Cổ", 21.0341)]
    public async Task LocateAsync_KnownLandmark_UsesItsPosition(string place, double latitude)
    {
        // STEP-01: Mock IPlaceSearchService.
        var search = new Mock<IPlaceSearchService>(MockBehavior.Strict);

        // STEP-02: Locate the stops; assert trip.DayAreas.Single().Latitude is latitude.
        var trip = await Locate(Resolver(search.Object), "Hà Nội", (1, place));

        trip!.DayAreas.Single().Latitude.Should().Be((decimal)latitude);
    }

    // TEST-16: A free day is the whole province and still counts as a day of the trip; covers several wordings.
    /// <summary>Replays "Ngày 3 : Tôi đi tự do", which used to bind the day to an unrelated point.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("tự do")]
    [InlineData("chưa biết")]
    [InlineData("tùy ý")]
    public async Task LocateAsync_FreeDay_IsWholeProvince(string? place)
    {
        // STEP-01: Mock IPlaceSearchService.
        var search = new Mock<IPlaceSearchService>(MockBehavior.Strict);

        // STEP-02: Locate the stops; assert trip.WholeProvinceDays equals 2, trip.DayAreas.Single().DayNumber is 1, trip.Unresolved is empty and 1 more.
        var trip = await Locate(Resolver(search.Object), "Hà Nội", (1, "Cầu Giấy"), (2, place));

        trip!.WholeProvinceDays.Should().Equal((short)2);
        trip.DayAreas.Single().DayNumber.Should().Be(1);
        trip.Unresolved.Should().BeEmpty();
        trip.LastDay.Should().Be(2);
    }

    // TEST-17: The district of the nearest restaurant is reported so a wrong location shows; it is left out when it repeats the name or the search is wide.
    /// <summary>Ensures "Hồ Gươm (khu Hà Đông)" would have been visible before any dish was chosen.</summary>
    [Fact]
    public async Task LocateAsync_District_ShownOnlyWhenItAddsSomething()
    {
        // STEP-01: Read landmark from the response and 1 more; assert landmark.DayAreas.Single().District is the expected text and 1 more.
        var landmark = await Locate(Resolver(null), "Hà Nội", (1, "Lăng Bác"));
        var district = await Locate(Resolver(null), "Hà Nội", (1, "Hoàn Kiếm"));

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

    /// <summary>Locates the stops as the model passes them, one day each, for a trip with the given province.</summary>
    private static Task<AgentTripPlaces> Locate(AgentPlaceResolver resolver, string? province, params (short Day, string? Place)[] stops) =>
        resolver.LocateAsync(null, stops.Select(stop => new AgentLocateStop([stop.Day], stop.Place)).ToArray(), new AgentState { Province = province }, Provinces, default);
}
