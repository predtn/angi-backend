using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ANGI.Application.Common.Interfaces.Repositories.Agent;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Common.Models.Discovery;
using ANGI.Application.Services.Agent.Conversation;
using ANGI.Application.Services.Agent.Intake;

namespace ANGI.Application.Services.Agent.Places;

/// <summary>
/// Locates places the user named, without a provider call, and applies them to the trip. Catalog addresses come first because they are free and exact for districts, wards and
/// streets; a map search covers landmarks, and its answer is used only when its name really contains what the user wrote.
/// </summary>
public sealed class AgentPlaceResolver(IAgentCatalogRepository catalog, IPlaceSearchService? search = null)
{
    // Bán kính nhỏ nhất đủ chỗ để chọn món cho mọi bữa; chạm 50 km là giới hạn validator cho phép.
    public static readonly decimal[] RadiusSteps = [3m, 5m, 8m, 12m, 20m, 30m, 50m];
    private const int EnoughRestaurants = 20;
    private const int MinPlaceLength = 5;
    private const int MaxProvinceAlias = 5;
    // Tên quen miệng mà bản đồ không có hoặc trả nhầm ("Lăng Bác" ra "Làng cổ Bắc Biên"): dùng đúng tọa độ của địa danh.
    // "Hồ Gươm, Hà Nội" từng ra "Hồ Gươm Plaza" ở Hà Đông, nên các địa danh hay gặp dùng tọa độ cố định thay vì hỏi bản đồ.
    private static readonly (decimal Latitude, decimal Longitude) Mausoleum = (21.0368m, 105.8347m);
    private static readonly (decimal Latitude, decimal Longitude) SwordLake = (21.0287m, 105.8524m);
    private static readonly (decimal Latitude, decimal Longitude) LiteratureTemple = (21.0293m, 105.8355m);
    private static readonly Dictionary<string, (decimal Latitude, decimal Longitude)> Landmarks = new(StringComparer.Ordinal)
    {
        ["lang bac"] = Mausoleum, ["lang chu tich"] = Mausoleum, ["lang ho chu tich"] = Mausoleum, ["lang ho chi minh"] = Mausoleum,
        ["ho guom"] = SwordLake, ["ho hoan kiem"] = SwordLake, ["bo ho"] = SwordLake,
        ["van mieu"] = LiteratureTemple, ["van mieu quoc tu giam"] = LiteratureTemple,
        ["pho co"] = (21.0341m, 105.8506m), ["pho co ha noi"] = (21.0341m, 105.8506m),
        ["ho tay"] = (21.0580m, 105.8240m), ["nha hat lon"] = (21.0243m, 105.8576m),
        ["hoang thanh thang long"] = (21.0352m, 105.8402m), ["chua mot cot"] = (21.0359m, 105.8336m)
    };
    private const double SamePlaceKm = 2;
    // Từ mở đầu tên một loại cơ sở (đã bỏ dấu). Cụm người dùng viết bắt đầu bằng một từ này thì kết quả bản đồ là một cơ sở đúng là thứ họ tìm.
    private static readonly string[] InstitutionWords =
    [
        "dai hoc", "truong", "hoc vien", "cao dang", "benh vien", "cho", "sieu thi", "trung tam thuong mai", "toa nha", "khu do thi", "san bay", "ga",
        "ben xe", "cong vien", "bao tang", "chua", "den", "nha tho", "khach san", "cong ty", "khu cong nghiep", "khu cong nghe cao", "san van dong",
        "nha van hoa", "thu vien"
    ];
    private static readonly string[] StreetWords = ["phố ", "pho ", "đường ", "duong "];
    // Bản đồ trả các loại này cho cả chuỗi vô nghĩa ("asdkjh, Hà Nội" ra "Hà Nội"): chúng không chỉ một nơi cụ thể.
    private static readonly HashSet<string> TooBroad = new(["place", "region", "country", "district"], StringComparer.OrdinalIgnoreCase);

    /// <summary>Locates the places as the model read them from the user's words; what cannot be located is returned as unresolved.</summary>
    /// <param name="trip">The area of the whole trip (no days), with the kind and parts the model read; null when stops are given.</param>
    public async Task<AgentTripPlaces> LocateAsync(AgentLocateStop? trip, AgentLocateStop[] stops, AgentState state, IReadOnlyCollection<string> provinces, CancellationToken ct)
    {
        // STEP-01: Load restaurants.
        var restaurants = await catalog.GetPublicPlacesAsync(ct);
        // STEP-02: When stops were read, return them located; a stop with no place is a day with no fixed place.
        if (stops.Length > 0)
        {
            var read = stops.Select(stop => new AgentItineraryStop(stop.Days ?? [], stop.Place?.Trim() ?? "", !string.IsNullOrWhiteSpace(stop.Place), stop.Kind, stop.Parts)).ToArray();
            return await LocateStopsAsync(read, state.Province, provinces, restaurants, ct) ?? new(state.Province, [], null, [], [], 0);
        }
        var tripArea = trip!.Place!.Trim();
        // STEP-03: Return the province alone when the area named is the province itself.
        if (IsProvince(tripArea, provinces)) return new(AgentIntakePolicy.FindProvince(AgentIntakePolicy.Plain(tripArea), provinces), [], null, [], [], 0);
        // STEP-04: Keep the province named with the area, even when the area itself is not found ("nội đô Hà Nội" on the first message).
        var province = state.Province ?? AgentIntakePolicy.FindProvince(AgentIntakePolicy.Plain(tripArea), provinces);
        // STEP-05: A zone the model read as several places is located place by place; when none is found the trip keeps the whole province.
        if (IsZone(trip.Kind, trip.Parts))
        {
            var (zone, report) = await ZoneAsync(tripArea, trip.Parts!, [], province, provinces, restaurants, ct);
            return zone is null ? new(province, [], null, [], [], 0, [report], TripAreaMissed: true) : new(zone.Province, [], zone, [], [], 0, [report]);
        }
        // STEP-06: Return the located area, or the name as unresolved.
        var area = await ResolveAsync(tripArea, province, true, restaurants, ct, trip.Kind);
        return area is null ? new(province, [], null, [], [tripArea], 0, TripAreaMissed: true) : new(area.Province, [], area, [], [], 0);
    }

    /// <summary>Locates a zone the model read as several places, for a dish suggestion; the report says what was located.</summary>
    public async Task<(AgentPlaceMatch? Match, AgentZoneReport Report)> LocateZoneAsync(string label, string[] parts, string? province, IReadOnlyCollection<string> provinces, CancellationToken ct) =>
        await ZoneAsync(label, parts, [], province ?? AgentIntakePolicy.FindProvince(AgentIntakePolicy.Plain(label), provinces), provinces, await catalog.GetPublicPlacesAsync(ct), ct);

    /// <summary>True when the model read the place as a zone and listed its places.</summary>
    public static bool IsZone(string? kind, string[]? parts) => kind == "zone" && parts?.Any(part => !string.IsNullOrWhiteSpace(part)) == true;

    /// <summary>How many places of one zone are located.</summary>
    public const int MaxZoneParts = 12;
    /// <summary>The widest circle one place of a zone gets; the zone is the places together, so one thin place does not pull in far restaurants.</summary>
    public const decimal ZonePartKm = 5m;

    /// <summary>
    /// Locates each place the model listed for a zone (catalog addresses and wards first, the map for the rest) and joins them into one area of several circles.
    /// The backend never reads the zone's name: the model said which places it holds.
    /// </summary>
    private async Task<(AgentPlaceMatch? Match, AgentZoneReport Report)> ZoneAsync(string label, string[] parts, short[] days, string? province,
        IReadOnlyCollection<string> provinces, AgentCatalogPlace[] restaurants, CancellationToken ct)
    {
        // STEP-01: Keep the distinct named places, leaving out the province itself, which is not a part of a zone inside it.
        var names = parts.Select(part => part?.Trim() ?? "").Where(part => part.Length > 0 && part.Length <= 100 && !IsProvince(part, provinces))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxZoneParts).ToArray();
        // STEP-02: Locate them together; the map service caches answers and is safe to call at once.
        var found = await Task.WhenAll(names.Select(name => ResolveAsync(name, province, true, restaurants, ct)));
        var located = names.Zip(found, (name, match) => (Name: name, Match: match)).ToArray();
        var circles = located.Where(x => x.Match is not null)
            .Select(x => new AgentZoneCircle(x.Match!.Label, x.Match.Latitude, x.Match.Longitude, Math.Min(x.Match.RadiusKm, ZonePartKm)))
            .DistinctBy(circle => (circle.Latitude, circle.Longitude)).ToArray();
        var missed = located.Where(x => x.Match is null).Select(x => x.Name).ToArray();
        // STEP-03: Return the zone with its report, or no zone when none of its places was found.
        var title = Label(label);
        var report = new AgentZoneReport(title, days, circles.Select(circle => circle.Label).ToArray(), missed, circles.Length > 0 ? "zone" : "province");
        if (circles.Length == 0) return (null, report);
        var first = circles[0];
        return (new AgentPlaceMatch(located.First(x => x.Match is not null).Match!.Province, title, first.Latitude, first.Longitude, first.RadiusKm, null, circles, missed), report);
    }

    private static string Label(string place) => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(place.Trim().ToLowerInvariant());

    private async Task<AgentTripPlaces?> LocateStopsAsync(AgentItineraryStop[] stops, string? province, IReadOnlyCollection<string> provinces, AgentCatalogPlace[] restaurants, CancellationToken ct)
    {
        // STEP-01: For each stop in stops.Where(filtered), leave the loop when ResolveAsync() exists.
        // Tỉnh nêu kèm nơi đến ("nội đô Hà Nội") được giữ trước tiên, kể cả khi nơi đó không tìm được.
        province ??= stops.Select(stop => AgentIntakePolicy.FindProvince(AgentIntakePolicy.Plain(stop.Place), provinces)).FirstOrDefault(found => found is not null);
        // Xác định tỉnh trước từ các nơi có trong địa chỉ quán, để kết quả không phụ thuộc thứ tự: "Ngày 1 Hòa Lạc, ngày 2 Hoàn Kiếm" cũng tra Hòa Lạc trong Hà Nội.
        foreach (var stop in stops.Where(stop => province is null && stop.Place.Length > 0 && !IsProvince(stop.Place, provinces) && !IsZone(stop.Kind, stop.Parts)))
            if (await ResolveAsync(stop.Place, null, false, restaurants, ct) is { } local) { province = local.Province; break; }
        // STEP-02: Prepare dayAreas as List<AgentDayArea>, wholeProvince as List<short>, unresolved as List<string> and zones as List<AgentZoneReport>.
        var dayAreas = new List<AgentDayArea>();
        var wholeProvince = new List<short>();
        var unresolved = new List<string>();
        var zones = new List<AgentZoneReport>();
        var missed = new List<short>();
        // STEP-03: Go through each stop in stops.
        foreach (var stop in stops)
        {
            // STEP-04: When the stop has no place, IsProvince(stop.Place, provinces) holds or IsOpenDay(stop.Place) holds, set province, call wholeProvince.AddRange and 1 more action(s).
            if (stop.Place.Length == 0 || IsProvince(stop.Place, provinces) || IsOpenDay(stop.Place))
            {
                if (stop.Place.Length > 0) province ??= AgentIntakePolicy.FindProvince(AgentIntakePolicy.Plain(stop.Place), provinces);
                wholeProvince.AddRange(stop.Days);
                continue;
            }
            // STEP-04a: A zone read as several places: its days search every located place, or the whole province when none was found.
            if (IsZone(stop.Kind, stop.Parts))
            {
                var (zone, report) = await ZoneAsync(stop.Place, stop.Parts!, stop.Days, province, provinces, restaurants, ct);
                zones.Add(report);
                if (zone is null) { missed.AddRange(stop.Days); continue; }
                province ??= zone.Province;
                dayAreas.AddRange(stop.Days.Select(day => new AgentDayArea(day, zone.Label, zone.Latitude, zone.Longitude, zone.RadiusKm, null, zone.Zone)));
                continue;
            }
            // STEP-05: Call ResolveAsync and keep match.
            var match = await ResolveAsync(stop.Place, province, stop.Explicit, restaurants, ct, stop.Kind);
            // STEP-06: Skip this round when match is missing.
            if (match is null)
            {
                // "ngày 1 cay nhẹ" không có động từ đi lại và không phải địa điểm: để luồng thường đọc, không báo là địa danh lạ.
                if (stop.Explicit) { unresolved.Add(stop.Place); missed.AddRange(stop.Days); }
                continue;
            }
            // STEP-07: Set province.
            province ??= match.Province;
            // STEP-08: Add the built entries to dayAreas.
            dayAreas.AddRange(stop.Days.Select(day => new AgentDayArea(day, match.Label, match.Latitude, match.Longitude, match.RadiusKm, match.District)));
        }
        // STEP-09: Return null early when dayAreas.Count is 0, wholeProvince.Count is 0 and unresolved.Count is 0.
        if (dayAreas.Count == 0 && wholeProvince.Count == 0 && unresolved.Count == 0 && zones.Count == 0) return null;
        // STEP-10: Prepare byDay through GroupBy(filtered).Select(filtered).ToArray and lastDay through DefaultIfEmpty().Max.
        // Mỗi ngày một nơi: tên nêu sau cùng là tên người dùng chốt.
        var byDay = dayAreas.GroupBy(x => x.DayNumber).Select(group => group.Last()).ToArray();
        // Ngày có nơi đến chưa tìm được vẫn thuộc chuyến đi: người dùng đã nêu ngày đó, chỉ là chọn quán trong toàn tỉnh.
        var lastDay = stops.Where(stop => stop.Explicit || stop.Days.Any(day => byDay.Any(x => x.DayNumber == day) || wholeProvince.Contains(day)))
            .SelectMany(stop => stop.Days).DefaultIfEmpty((short)0).Max();
        // STEP-11: Return the new result.
        // A day both named and missed keeps the place found for it (the name given last wins, as for byDay).
        var missedDays = missed.Distinct().Where(day => byDay.All(x => x.DayNumber != day)).ToArray();
        return new(province, byDay, null, wholeProvince.Distinct().ToArray(), unresolved.Distinct().ToArray(), lastDay, zones.Count > 0 ? zones.ToArray() : null,
            missedDays.Length > 0 ? missedDays : null);
    }

    /// <summary>The area around a point pinned on the map, at least the default radius wide; null when no restaurant lies within the largest radius.</summary>
    public async Task<AgentPlaceMatch?> AroundPointAsync(decimal latitude, decimal longitude, CancellationToken ct) =>
        Sized("", latitude, longitude, await catalog.GetPublicPlacesAsync(ct)) is { } area ? area with { RadiusKm = Math.Max(area.RadiusKm, AgentIntakePolicy.DefaultRadiusKm) } : null;

    /// <summary>Locates one named area inside the trip's province; null when it cannot be located or is the whole province.</summary>
    /// <param name="kind">How the model read the place: street, ward, landmark or zone; null when it did not say.</param>
    public async Task<AgentPlaceMatch?> LocateAsync(string place, string? province, bool searchMap, IReadOnlyCollection<string> provinces, CancellationToken ct, string? kind = null) =>
        IsProvince(place, provinces) ? null : await ResolveAsync(place, province ?? AgentIntakePolicy.FindProvince(AgentIntakePolicy.Plain(place), provinces), searchMap,
            await catalog.GetPublicPlacesAsync(ct), ct, kind);

    private async Task<AgentPlaceMatch?> ResolveAsync(string place, string? province, bool searchMap, AgentCatalogPlace[] restaurants, CancellationToken ct, string? kind = null)
    {
        // STEP-01: Prepare term through WithoutProvince.
        var term = WithoutProvince(AgentIntakePolicy.Plain(place), province);
        // STEP-02: Return null early when term.Length < MinPlaceLength or IsOpenDay(place) holds.
        if (term.Length < MinPlaceLength || IsOpenDay(place)) return null;
        // A whole province or city the model recognized ("Đà Nẵng") is never searched as a place inside the trip's province, where the map offers any business
        // carrying its name. A catalog province was already taken as the whole province before this; any other has no data on ANGI.
        if (kind == "province") return null;
        // STEP-03: Prepare inProvince from restaurants.Where and label through InvariantCulture.TextInfo.ToTitleCase.
        var inProvince = province is null ? restaurants : restaurants.Where(x => ProvinceSearch.Same(x.Province, province)).ToArray();
        var label = Label(place);
        // STEP-04: Return the result of Sized early when Landmarks has an entry for term.
        if (Landmarks.TryGetValue(term, out var landmark)) return Sized(label, landmark.Latitude, landmark.Longitude, inProvince);
        // STEP-05: Prepare lowered through place.Trim().ToLowerInvariant and street through StreetWords.FirstOrDefault.
        // "phố Huế" là con đường ở Hà Nội; "Huế" là thành phố khác.
        var lowered = place.Trim().ToLowerInvariant();
        var street = StreetWords.FirstOrDefault(word => lowered.StartsWith(word, StringComparison.Ordinal));
        // STEP-06: Prepare addressed through AddressMatches.
        var addressed = AddressMatches(term, inProvince);
        // STEP-07: When addressed.Length is 0 and street exists, set addressed.
        if (addressed.Length == 0 && street is not null) addressed = AddressMatches(AgentIntakePolicy.Plain(lowered[street.Length..]), inProvince);
        // STEP-08: When addressed.Length > 0, prepare latitude through Median, prepare longitude through Median and 1 more action(s).
        if (addressed.Length > 0)
        {
            var latitude = Median(addressed.Select(x => x.Latitude));
            var longitude = Median(addressed.Select(x => x.Longitude));
            return Sized(label, latitude, longitude, inProvince);
        }
        // STEP-09: Return null early when searchMap is false or search is missing.
        if (!searchMap || search is null) return null;
        // STEP-10: Prepare nearLatitude, nearLongitude, catalogProvinces and searchIn, then load candidates through search.SearchAsync.
        var nearLatitude = inProvince.Length > 0 ? Median(inProvince.Select(x => x.Latitude)) : (decimal?)null;
        var nearLongitude = inProvince.Length > 0 ? Median(inProvince.Select(x => x.Longitude)) : (decimal?)null;
        // ANGI chỉ có dữ liệu ở một tỉnh thì tra trong tỉnh đó: "Hòa Lạc, Việt Nam" có thể ra nơi trùng tên ở tỉnh khác.
        var catalogProvinces = restaurants.Select(x => x.Province.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToArray();
        var searchIn = province ?? (catalogProvinces.Length == 1 ? catalogProvinces[0] : "Việt Nam");
        // A street the model recognized is asked as a street first: "Xã Đàn, Hà Nội" alone makes the map look for a commune ("xã") and miss Phố Xã Đàn.
        string[] queries = kind == "street" && street is null ? [$"Phố {place.Trim()}", $"Đường {place.Trim()}", place.Trim()] : [place.Trim()];
        foreach (var query in queries)
        {
            var candidates = await search.SearchAsync($"{query}, {searchIn}", nearLatitude, nearLongitude, ct);
            // STEP-11: Return the located place when Verified() exists.
            // Bản đồ luôn trả về thứ gì đó; chỉ tin khi tên hoặc địa chỉ chứa nguyên cụm người dùng viết.
            if (Verified(candidates, term, province) is { } found) return Sized(label, found.Latitude, found.Longitude, inProvince);
        }
        return null;
    }

    /// <summary>
    /// Trusts a map answer only with evidence, because a business can carry any place's name ("Hồ Gươm Plaza" is in Hà Đông). An area or street containing the
    /// phrase counts, as does a business named exactly that; otherwise most businesses with the phrase in their name must sit together, like the shops of Times City.
    /// </summary>
    private static (decimal Latitude, decimal Longitude)? Verified(AgentPlaceCandidate[] candidates, string term, string? province)
    {
        // STEP-01: Prepare usable through candidates.Where(filtered).ToArray.
        var usable = candidates.Where(x => !TooBroad.Contains(x.Type ?? "")).ToArray();
        // STEP-02: Return the collected values early when usable.FirstOrDefault(filtered) exists.
        if (usable.FirstOrDefault(x => !IsBusiness(x) && $" {AgentIntakePolicy.Plain($"{x.Name} {x.Address}")} ".Contains($" {term} ", StringComparison.Ordinal)) is { } area)
            return (area.Latitude, area.Longitude);
        // STEP-03: Prepare named through usable.Where(filtered).ToArray.
        var named = usable.Where(x => IsBusiness(x) && $" {AgentIntakePolicy.Plain(x.Name)} ".Contains($" {term} ", StringComparison.Ordinal)).ToArray();
        // STEP-04: Return the collected values early when named.FirstOrDefault(filtered) exists.
        if (named.FirstOrDefault(x => WithoutProvince(AgentIntakePolicy.Plain(x.Name), province) == term) is { } exact) return (exact.Latitude, exact.Longitude);
        // Người dùng gọi tên một cơ sở theo loại ("Đại học FPT", "Bệnh viện Bạch Mai"): kết quả là một cơ sở duy nhất có tên BẮT ĐẦU bằng đúng cụm đó
        // thì chính là nơi họ tìm, dù tên dài hơn ("Đại học FPT Hà Nội (Cơ sở Hòa Lạc)", hội thoại 222). Một tên trống không có loại ("Thủy Tiên")
        // vẫn không tin cơ sở mượn tên ("Thủy Tiên Plaza"), và cụm chỉ nằm giữa tên ("Ký túc xá gần Đại học FPT") vẫn bị từ chối.
        if (named.Length == 1 && InstitutionWords.Any(kind => $"{term} ".StartsWith($"{kind} ", StringComparison.Ordinal)) &&
            $"{WithoutProvince(AgentIntakePolicy.Plain(named[0].Name), province)} ".StartsWith($"{term} ", StringComparison.Ordinal))
            return (named[0].Latitude, named[0].Longitude);
        // STEP-05: Prepare cluster through named.Select(filtered).MaxBy.
        var cluster = named.Select(x => named.Where(other => NearbyArea.DistanceKm(x.Latitude, x.Longitude, other.Latitude, other.Longitude) <= SamePlaceKm).ToArray())
            .MaxBy(group => group.Length);
        // STEP-06: Return one of two results depending on cluster has the expected shape and cluster.Length * 2 > named.Length.
        return cluster is { Length: >= 2 } && cluster.Length * 2 > named.Length ? (Median(cluster.Select(x => x.Latitude)), Median(cluster.Select(x => x.Longitude))) : null;
    }

    private static bool IsBusiness(AgentPlaceCandidate candidate) => string.Equals(candidate.Type, "poi", StringComparison.OrdinalIgnoreCase);

    /// <summary>Picks the smallest radius holding enough restaurants; null when none lies within the largest radius, whatever the map says.</summary>
    private static AgentPlaceMatch? Sized(string label, decimal latitude, decimal longitude, AgentCatalogPlace[] restaurants)
    {
        // STEP-01: Prepare distances through Select(filtered).OrderBy(filtered).ToArray and largest.
        var distances = restaurants.Select(x => (x.Province, x.Address, Km: NearbyArea.DistanceKm(latitude, longitude, x.Latitude, x.Longitude))).OrderBy(x => x.Km).ToArray();
        var largest = RadiusSteps[^1];
        // STEP-02: Return null early when distances.Length is 0 or distances[0].Km > largest.
        if (distances.Length == 0 || distances[0].Km > (double)largest) return null;
        // STEP-03: Prepare radius through RadiusSteps.FirstOrDefault.
        var radius = RadiusSteps.FirstOrDefault(step => distances.Count(x => x.Km <= (double)step) >= EnoughRestaurants, largest);
        // STEP-04: Return the new result.
        return new(distances[0].Province, label, decimal.Round(latitude, 6), decimal.Round(longitude, 6), radius,
            radius > WideRadiusKm ? null : District(distances[0].Address, label));
    }

    /// <summary>The district of the nearest restaurant, from the second to last address part; null when it would only repeat the place's own name.</summary>
    private static string? District(string? address, string label)
    {
        // STEP-01: Prepare parts through address ?? "".Split.
        var parts = (address ?? "").Split(',');
        // STEP-02: Return null early when parts.Length < 2.
        if (parts.Length < 2) return null;
        // STEP-03: Prepare district through parts[^2].Trim.
        var district = parts[^2].Trim();
        // STEP-04: Return one of two results depending on district.Length is 0 or AgentIntakePolicy.Plain() is AgentIntakePolicy.Plain().
        return district.Length == 0 || AgentIntakePolicy.Plain(district) == AgentIntakePolicy.Plain(label) ? null : district;
    }

    /// <summary>Prefers a whole address part ("..., Hoàn Kiếm, Hà Nội"); a name found inside a longer part, such as a street, is the fallback.</summary>
    /// <remarks>The ward is a part too: production addresses hold only the street, and the ward ("Phường Kim Liên") is stored apart.</remarks>
    private static AgentCatalogPlace[] AddressMatches(string term, AgentCatalogPlace[] restaurants)
    {
        // STEP-01: Prepare addressed through Select(filtered).Where(filtered).ToArray and whole through Where(filtered).Select(filtered).ToArray.
        var addressed = restaurants.Select(x => (Place: x, Parts: (x.Address ?? "").Split(',').Append(x.Ward ?? "").Where(part => !string.IsNullOrWhiteSpace(part))
            .SelectMany(AddressPart).ToArray())).Where(x => x.Parts.Length > 0).ToArray();
        var whole = addressed.Where(x => x.Parts.Contains(term)).Select(x => x.Place).ToArray();
        // STEP-02: Return one of two results depending on whole.Length > 0.
        return whole.Length > 0 ? whole : addressed.Where(x => x.Parts.Any(part => $" {part} ".Contains($" {term} ", StringComparison.Ordinal))).Select(x => x.Place).ToArray();
    }

    /// <summary>
    /// One address part as written and without its administrative word, so "Phường Hoàn Kiếm" matches "Hoàn Kiếm" while a street that only
    /// starts like one ("Xã Đàn") still matches its whole name.
    /// </summary>
    private static IEnumerable<string> AddressPart(string part)
    {
        // STEP-01: Prepare words through AgentIntakePolicy.Plain and return them.
        var words = AgentIntakePolicy.Plain(part);
        yield return words;
        // STEP-02: Return the words without the first administrative prefix they start with.
        if (new[] { "p ", "q ", "x ", "tt ", "phuong ", "quan ", "xa ", "huyen ", "thi tran " }.FirstOrDefault(prefix => words.StartsWith(prefix, StringComparison.Ordinal)) is { } found)
            yield return words[found.Length..];
    }

    private static decimal Median(IEnumerable<decimal> values)
    {
        var ordered = values.Order().ToArray();
        return ordered[ordered.Length / 2];
    }

    /// <summary>The name is the province itself ("Hà Nội", "HN"), which means the whole province rather than an area inside it.</summary>
    public static bool IsProvince(string place, IReadOnlyCollection<string> provinces)
    {
        var words = AgentIntakePolicy.Plain(place);
        // "Hà Nội" is the catalog's "Thành phố Hà Nội": the name without its administrative prefix counts too. Its 6 letters were over the short-alias limit,
        // so a province read as "Hà Nội" was taken for a province without data (replay of conversation 16: "ANGI chưa có nhà hàng ở Hà Nội").
        return AgentIntakePolicy.FindProvince(words, provinces) is { } province && (words == AgentIntakePolicy.Plain(province) ||
            AgentIntakePolicy.Bare(words) == AgentIntakePolicy.Bare(AgentIntakePolicy.Plain(province)) || words.Length <= MaxProvinceAlias);
    }

    /// <summary>Drops a trailing province, full or short ("Thành phố Hà Nội" or "Hà Nội"), so "Hoàn Kiếm Hà Nội" is looked up as "Hoàn Kiếm".</summary>
    private static string WithoutProvince(string words, string? province)
    {
        if (province is null) return words;
        var full = AgentIntakePolicy.Plain(province);
        var prefix = new[] { "thanh pho ", "tinh " }.FirstOrDefault(value => full.StartsWith(value, StringComparison.Ordinal));
        foreach (var name in prefix is null ? [full] : new[] { full, full[prefix.Length..] })
            if (words.EndsWith($" {name}", StringComparison.Ordinal)) return words[..^(name.Length + 1)];
        return words;
    }

    // Applying places to the trip

    /// <summary>Above this radius the place has few restaurants nearby, and the reply says how far the search reaches.</summary>
    public const decimal WideRadiusKm = 12m;

    // Ngày không có nơi cụ thể: chọn quán trong toàn tỉnh. "Tự Do" không phải địa danh, tra bản đồ sẽ ra chỗ bất kỳ ("tủ đồ" bỏ dấu cũng là "tu do").
    private static readonly Regex OpenDay = new(@"^(?:tu do|tuy y|tuy thich|chua biet|chua ro|chua co ke hoach|khong co ke hoach|chua quyet dinh|linh hoat|dau cung duoc|di dau cung duoc|o dau cung duoc|nghi ngoi)$", RegexOptions.CultureInvariant);

    /// <summary>Applies the located places: days move to them, and the trip grows when a later day was named.</summary>
    public static AgentState Apply(AgentState state, AgentTripPlaces trip)
    {
        // STEP-01: Prepare stated through DayAreas.Select(filtered).Concat().ToHashSet, areas through Concat().OrderBy(filtered).ToArray and more.
        // A day whose new place was not found uses the whole province, as the note says, so its earlier place goes too (feedback 2026-11-25).
        var stated = trip.DayAreas.Select(x => x.DayNumber).Concat(trip.WholeProvinceDays).Concat(trip.MissedDays ?? []).ToHashSet();
        var areas = (state.DayAreas ?? []).Where(x => !stated.Contains(x.DayNumber)).Concat(trip.DayAreas).OrderBy(x => x.DayNumber).ToArray();
        // Ngày đã chuyển nơi thì tìm lại từ đầu, bán kính đã nới cho nơi cũ không còn giá trị.
        var widened = trip.TripArea is not null ? [] : (state.WidenedMeals ?? []).Where(x => !stated.Contains(x.DayNumber)).ToArray();
        var applied = state with
        {
            Province = state.Province ?? trip.Province,
            NumDays = trip.LastDay > (state.NumDays ?? 0) ? trip.LastDay : state.NumDays,
            DayAreas = areas.Length > 0 ? areas : null,
            WidenedMeals = widened.Length > 0 ? widened : null
        };
        // STEP-02: Return one of two results depending on trip.TripArea exists.
        // A whole-trip area that was not found replaces an earlier area the backend located from the user's words with the whole province, as the note
        // says; a point picked on the map or GPS is the user's own choice and stays.
        if (trip.TripAreaMissed && state.OriginMode == "pinned" && state.NamedAreaLabel is not null && state.NamedAreaLabel == state.OriginLabel)
            applied = applied with { OriginMode = "none", OriginLatitude = null, OriginLongitude = null, OriginLabel = null, RadiusKm = null, NamedAreaLabel = null,
                OriginZone = null, WidenedMeals = null };
        // Một vùng nhiều nơi lưu đủ các vòng tròn; một nơi thì xóa vùng cũ.
        return trip.TripArea is { } area ? applied with { OriginMode = "pinned", OriginLatitude = area.Latitude, OriginLongitude = area.Longitude,
            OriginLabel = area.Label, RadiusKm = area.RadiusKm, NamedAreaLabel = area.Label, OriginZone = area.Zone } : applied;
    }

    /// <summary>One day of the itinerary for chat; says how far the search reaches when the place has few restaurants nearby.</summary>
    public static string Describe(AgentDayArea area) => area.Zone is { Length: > 1 } ? $"ngày {area.DayNumber} trong khu {AgentReplyBuilder.Safe(area.Label)}" :
        $"ngày {area.DayNumber} quanh {AgentReplyBuilder.Safe(area.Label)}" +
        (area.District is { } district ? $" (khu {AgentReplyBuilder.Safe(district)})" : "") +
        (area.RadiusKm > WideRadiusKm ? $" (quanh đó còn ít quán nên mình chọn quán gần nhất, trong khoảng {area.RadiusKm:0} km)" : "");

    /// <summary>True when a day has no fixed place ("đi tự do", "chưa biết"), so its dishes come from the whole province.</summary>
    public static bool IsOpenDay(string place) => OpenDay.IsMatch(Fold(place.Normalize(NormalizationForm.FormC)).Trim());

    /// <summary>One sentence per place, meal and radius for the meals of this plan that had to look further out than their place's radius.</summary>
    public static IEnumerable<string> WidenedNotes(AgentSnapshot snapshot)
    {
        var state = snapshot.State;
        return (state.WidenedMeals ?? []).Where(meal => snapshot.Items.Any(item => item.DayNumber == meal.DayNumber && item.MealSlot == meal.MealSlot))
            .Select(meal =>
            {
                var place = state.DayAreas?.FirstOrDefault(area => area.DayNumber == meal.DayNumber);
                return (Meal: meal, Label: place?.Label ?? state.NamedAreaLabel, Radius: place?.RadiusKm ?? state.RadiusKm);
            })
            .Where(x => x.Label is not null && x.Radius.HasValue)
            .GroupBy(x => (x.Label, x.Radius, x.Meal.MealSlot, x.Meal.RadiusKm))
            .Select(group => $"Quanh {AgentReplyBuilder.Safe(group.Key.Label)} trong {group.Key.Radius:0} km chưa có món cho {AgentReplyBuilder.Meal(group.Key.MealSlot)} " +
                $"ngày {string.Join(", ", group.Select(x => x.Meal.DayNumber).Order())}, nên bữa đó mình chọn quán trong khoảng {group.Key.RadiusKm:0} km.");
    }

    /// <summary>True when the name is a place the itinerary already holds, so it must not be searched as a dish or restaurant.</summary>
    public static bool IsKnownPlace(AgentState state, string name)
    {
        var folded = Fold(name.Normalize(NormalizationForm.FormC)).Trim();
        return (state.DayAreas ?? []).Select(x => x.Label).Append(state.OriginLabel).OfType<string>().Any(label => Fold(label.Normalize(NormalizationForm.FormC)) == folded);
    }

    /// <summary>Lowercases and removes accents one character at a time, so positions match the original text and labels keep the user's spelling.</summary>
    internal static string Fold(string text) => string.Create(text.Length, text, (target, source) =>
    {
        for (var index = 0; index < source.Length; index++)
        {
            var lower = char.ToLowerInvariant(source[index]);
            target[index] = lower == 'đ' ? 'd' : lower.ToString().Normalize(NormalizationForm.FormD)[0];
        }
    });
}
