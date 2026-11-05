using System.Globalization;
using ANGI.Application.Common.Interfaces.Repositories.Agent;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Common.Models.Discovery;
using ANGI.Application.Services.Agent.Intake;

namespace ANGI.Application.Services.Agent.Places;

/// <summary>
/// Locates places the user named, without a provider call. Catalog addresses come first because they are free and exact for districts, wards and
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
    // Nơi ngoài dữ liệu ANGI. Bản đồ được hỏi "Đà Nẵng, Hà Nội" sẽ trả một địa điểm ở Hà Nội có tên chứa chữ đó, nên phải chặn trước khi hỏi bản đồ.
    // Tỉnh đã có quán được nhận ra sớm hơn ở IsProvince, nên khi dữ liệu mở rộng thì danh sách này không cản.
    private static readonly HashSet<string> Elsewhere = new(StringComparer.Ordinal)
    {
        "an giang", "ba ria vung tau", "bac giang", "bac kan", "bac lieu", "bac ninh", "ben tre", "binh dinh", "binh duong", "binh phuoc", "binh thuan", "ca mau", "can tho",
        "cao bang", "da nang", "dak lak", "dak nong", "dien bien", "dong nai", "dong thap", "gia lai", "ha giang", "ha nam", "ha tinh", "hai duong", "hai phong", "hau giang",
        "ho chi minh", "sai gon", "tphcm", "hoa binh", "hung yen", "khanh hoa", "kien giang", "kon tum", "lai chau", "lam dong", "lang son", "lao cai", "long an", "nam dinh",
        "nghe an", "ninh binh", "ninh thuan", "phu tho", "phu yen", "quang binh", "quang nam", "quang ngai", "quang ninh", "quang tri", "soc trang", "son la", "tay ninh",
        "thai binh", "thai nguyen", "thanh hoa", "thua thien hue", "hue", "tien giang", "tra vinh", "tuyen quang", "vinh long", "vinh phuc", "yen bai",
        "sa pa", "sapa", "nha trang", "phu quoc", "da lat", "hoi an", "ha long", "vung tau", "mui ne", "phan thiet", "quy nhon", "cat ba", "tam dao", "moc chau", "con dao", "ly son", "buon ma thuot", "pleiku", "vinh"
    };
    private static readonly string[] StreetWords = ["phố ", "pho ", "đường ", "duong "];
    // Bản đồ trả các loại này cho cả chuỗi vô nghĩa ("asdkjh, Hà Nội" ra "Hà Nội"): chúng không chỉ một nơi cụ thể.
    private static readonly HashSet<string> TooBroad = new(["place", "region", "country", "district"], StringComparer.OrdinalIgnoreCase);

    /// <summary>Reads the places of the current message; null when it names none, or nothing that could be located and was clearly meant as a place.</summary>
    public async Task<AgentTripPlaces?> ReadAsync(AgentContext context, IReadOnlyCollection<string> provinces, CancellationToken ct)
    {
        // STEP-01: Prepare state, content, stops through AgentItineraryPolicy.Stops, opened and 5 more.
        var state = context.State;
        var content = context.Request.Content;
        var stops = AgentItineraryPolicy.Stops(content);
        var opened = state.PlanningIntake is not null;
        var asked = opened && (string.IsNullOrWhiteSpace(state.Province) || state.PlanningIntake == AgentIntakePolicy.Scope);
        var planning = opened || AgentIntakePolicy.StatesPlan(content);
        // Chưa có lịch mà chỉ nói "ở Hoàn Kiếm": hiểu là khu vực của cả chuyến và bắt đầu hỏi tiếp.
        var bare = stops.Length > 0 || planning ? null : AgentItineraryPolicy.BareArea(content);
        // "vị trí hiện tại" và "toàn thành phố" không phải tên địa điểm, không tra bản đồ.
        var notAPlace = AgentIntakePolicy.StatesHere(content) || state.PlanningIntake == AgentIntakePolicy.Scope && AgentIntakePolicy.StatesWholeProvince(content, state.Province);
        var areaName = stops.Length > 0 || notAPlace ? null : planning ? AgentItineraryPolicy.TripArea(content, asked) : bare?.Name;
        // STEP-02: Return null early when stops.Length is 0 and areaName is missing.
        if (stops.Length == 0 && areaName is null) return null;
        // STEP-03: Load restaurants through catalog.GetPublicPlacesAsync.
        var restaurants = await catalog.GetPublicPlacesAsync(ct);
        // STEP-04: Prepare province from AgentIntakePolicy.Plain.
        var province = state.Province ?? AgentIntakePolicy.FindProvince(AgentIntakePolicy.Plain(content), provinces);

        // STEP-05: When stops.Length is 0, return null early when IsProvince(provinces, ...) holds, prepare area through ResolveAsync and 1 more action(s).
        if (stops.Length == 0)
        {
            if (IsProvince(areaName!, provinces)) return null;
            var area = await ResolveAsync(areaName!, province, planning || bare?.Searchable == true, restaurants, ct);
            return area is null ? null : new(area.Province, [], area, [], [], 0);
        }

        // STEP-06: For each stop in stops.Where(filtered), leave the loop when ResolveAsync() exists.
        // Xác định tỉnh trước từ các nơi có trong địa chỉ quán, để kết quả không phụ thuộc thứ tự: "Ngày 1 Hòa Lạc, ngày 2 Hoàn Kiếm" cũng tra Hòa Lạc trong Hà Nội.
        foreach (var stop in stops.Where(stop => province is null && !IsProvince(stop.Place, provinces)))
            if (await ResolveAsync(stop.Place, null, false, restaurants, ct) is { } local) { province = local.Province; break; }
        // STEP-07: Prepare dayAreas as List<AgentDayArea>, wholeProvince as List<short> and unresolved as List<string>.
        var dayAreas = new List<AgentDayArea>();
        var wholeProvince = new List<short>();
        var unresolved = new List<string>();
        // STEP-08: Go through each stop in stops.
        foreach (var stop in stops)
        {
            // STEP-09: When IsProvince(stop.Place, provinces) holds or AgentItineraryPolicy.IsOpenDay(stop.Place) holds, set province, call wholeProvince.AddRange and 1 more action(s).
            if (IsProvince(stop.Place, provinces) || AgentItineraryPolicy.IsOpenDay(stop.Place))
            {
                province ??= AgentIntakePolicy.FindProvince(AgentIntakePolicy.Plain(stop.Place), provinces);
                wholeProvince.AddRange(stop.Days);
                continue;
            }
            // STEP-10: Call ResolveAsync and keep match.
            var match = await ResolveAsync(stop.Place, province, stop.Explicit, restaurants, ct);
            // STEP-11: Skip this round when match is missing.
            if (match is null)
            {
                // "ngày 1 cay nhẹ" không có động từ đi lại và không phải địa điểm: để luồng thường đọc, không báo là địa danh lạ.
                if (stop.Explicit) unresolved.Add(stop.Place);
                continue;
            }
            // STEP-12: Set province.
            province ??= match.Province;
            // STEP-13: Add the built entries to dayAreas.
            dayAreas.AddRange(stop.Days.Select(day => new AgentDayArea(day, match.Label, match.Latitude, match.Longitude, match.RadiusKm, match.District)));
        }
        // STEP-14: Return null early when dayAreas.Count is 0, wholeProvince.Count is 0 and unresolved.Count is 0.
        if (dayAreas.Count == 0 && wholeProvince.Count == 0 && unresolved.Count == 0) return null;
        // STEP-15: Prepare byDay through GroupBy(filtered).Select(filtered).ToArray and lastDay through DefaultIfEmpty().Max.
        // Mỗi ngày một nơi: tên nêu sau cùng là tên người dùng chốt.
        var byDay = dayAreas.GroupBy(x => x.DayNumber).Select(group => group.Last()).ToArray();
        // Ngày có nơi đến chưa tìm được vẫn thuộc chuyến đi: người dùng đã nêu ngày đó, chỉ là chọn quán trong toàn tỉnh.
        var lastDay = stops.Where(stop => stop.Explicit || stop.Days.Any(day => byDay.Any(x => x.DayNumber == day) || wholeProvince.Contains(day)))
            .SelectMany(stop => stop.Days).DefaultIfEmpty((short)0).Max();
        // STEP-16: Return the new result.
        return new(province, byDay, null, wholeProvince.Distinct().ToArray(), unresolved.Distinct().ToArray(), lastDay);
    }

    /// <summary>The area around a point pinned on the map, at least the default radius wide; null when no restaurant lies within the largest radius.</summary>
    public async Task<AgentPlaceMatch?> AroundPointAsync(decimal latitude, decimal longitude, CancellationToken ct) =>
        Sized("", latitude, longitude, await catalog.GetPublicPlacesAsync(ct)) is { } area ? area with { RadiusKm = Math.Max(area.RadiusKm, AgentIntakePolicy.DefaultRadiusKm) } : null;

    /// <summary>Locates one named area inside the trip's province; null when it cannot be located or is the whole province.</summary>
    public async Task<AgentPlaceMatch?> LocateAsync(string place, string? province, bool searchMap, IReadOnlyCollection<string> provinces, CancellationToken ct) =>
        IsProvince(place, provinces) ? null : await ResolveAsync(place, province, searchMap, await catalog.GetPublicPlacesAsync(ct), ct);

    private async Task<AgentPlaceMatch?> ResolveAsync(string place, string? province, bool searchMap, AgentCatalogPlace[] restaurants, CancellationToken ct)
    {
        // STEP-01: Prepare term through WithoutProvince.
        var term = WithoutProvince(AgentIntakePolicy.Plain(place), province);
        // STEP-02: Return null early when term.Length < MinPlaceLength or AgentItineraryPolicy.IsOpenDay(place) holds.
        if (term.Length < MinPlaceLength || AgentItineraryPolicy.IsOpenDay(place)) return null;
        // STEP-03: Prepare inProvince from restaurants.Where and label through InvariantCulture.TextInfo.ToTitleCase.
        var inProvince = province is null ? restaurants : restaurants.Where(x => string.Equals(x.Province.Trim(), province.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        var label = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(place.Trim().ToLowerInvariant());
        // STEP-04: Return the result of Sized early when Landmarks has an entry for term.
        if (Landmarks.TryGetValue(term, out var landmark)) return Sized(label, landmark.Latitude, landmark.Longitude, inProvince);
        // STEP-05: Prepare lowered through place.Trim().ToLowerInvariant and street through StreetWords.FirstOrDefault.
        // "phố Huế" là con đường ở Hà Nội; "Huế" là thành phố khác.
        var lowered = place.Trim().ToLowerInvariant();
        var street = StreetWords.FirstOrDefault(word => lowered.StartsWith(word, StringComparison.Ordinal));
        // STEP-06: Return null early when street is missing and Elsewhere contains the conditional value.
        if (street is null && Elsewhere.Contains(term.StartsWith("thanh pho ", StringComparison.Ordinal) ? term["thanh pho ".Length..] : term.StartsWith("tinh ", StringComparison.Ordinal) ? term["tinh ".Length..] : term)) return null;
        // STEP-07: Prepare addressed through AddressMatches.
        var addressed = AddressMatches(term, inProvince);
        // STEP-08: When addressed.Length is 0 and street exists, set addressed.
        if (addressed.Length == 0 && street is not null) addressed = AddressMatches(AgentIntakePolicy.Plain(lowered[street.Length..]), inProvince);
        // STEP-09: When addressed.Length > 0, prepare latitude through Median, prepare longitude through Median and 1 more action(s).
        if (addressed.Length > 0)
        {
            var latitude = Median(addressed.Select(x => x.Latitude));
            var longitude = Median(addressed.Select(x => x.Longitude));
            return Sized(label, latitude, longitude, inProvince);
        }
        // STEP-10: Return null early when searchMap is false or search is missing.
        if (!searchMap || search is null) return null;
        // STEP-11: Prepare nearLatitude, nearLongitude, catalogProvinces and searchIn, then load candidates through search.SearchAsync.
        var nearLatitude = inProvince.Length > 0 ? Median(inProvince.Select(x => x.Latitude)) : (decimal?)null;
        var nearLongitude = inProvince.Length > 0 ? Median(inProvince.Select(x => x.Longitude)) : (decimal?)null;
        // ANGI chỉ có dữ liệu ở một tỉnh thì tra trong tỉnh đó: "Hòa Lạc, Việt Nam" có thể ra nơi trùng tên ở tỉnh khác.
        var catalogProvinces = restaurants.Select(x => x.Province.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToArray();
        var searchIn = province ?? (catalogProvinces.Length == 1 ? catalogProvinces[0] : "Việt Nam");
        var candidates = await search.SearchAsync($"{place.Trim()}, {searchIn}", nearLatitude, nearLongitude, ct);
        // STEP-12: Return one of two results depending on Verified() exists.
        // Bản đồ luôn trả về thứ gì đó; chỉ tin khi tên hoặc địa chỉ chứa nguyên cụm người dùng viết.
        return Verified(candidates, term, province) is { } found ? Sized(label, found.Latitude, found.Longitude, inProvince) : null;
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
            radius > AgentItineraryPolicy.WideRadiusKm ? null : District(distances[0].Address, label));
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
    private static AgentCatalogPlace[] AddressMatches(string term, AgentCatalogPlace[] restaurants)
    {
        // STEP-01: Prepare addressed through Where(filtered).Select(filtered).ToArray and whole through Where(filtered).Select(filtered).ToArray.
        var addressed = restaurants.Where(x => !string.IsNullOrWhiteSpace(x.Address)).Select(x => (Place: x, Parts: x.Address!.Split(',').Select(AddressPart).ToArray())).ToArray();
        var whole = addressed.Where(x => x.Parts.Contains(term)).Select(x => x.Place).ToArray();
        // STEP-02: Return one of two results depending on whole.Length > 0.
        return whole.Length > 0 ? whole : addressed.Where(x => x.Parts.Any(part => $" {part} ".Contains($" {term} ", StringComparison.Ordinal))).Select(x => x.Place).ToArray();
    }

    private static string AddressPart(string part)
    {
        // STEP-01: Prepare words through AgentIntakePolicy.Plain.
        var words = AgentIntakePolicy.Plain(part);
        // STEP-02: For each prefix in the computed value, return words[prefix.Length..] early when words starts with prefix.
        foreach (var prefix in new[] { "p ", "q ", "x ", "tt ", "phuong ", "quan ", "xa ", "huyen ", "thi tran " })
            if (words.StartsWith(prefix, StringComparison.Ordinal)) return words[prefix.Length..];
        // STEP-03: Return words.
        return words;
    }

    private static decimal Median(IEnumerable<decimal> values)
    {
        var ordered = values.Order().ToArray();
        return ordered[ordered.Length / 2];
    }

    /// <summary>The name is the province itself ("Hà Nội", "HN"), which means the whole province rather than an area inside it.</summary>
    private static bool IsProvince(string place, IReadOnlyCollection<string> provinces)
    {
        var words = AgentIntakePolicy.Plain(place);
        return AgentIntakePolicy.FindProvince(words, provinces) is { } province && (words == AgentIntakePolicy.Plain(province) || words.Length <= MaxProvinceAlias);
    }

    /// <summary>Drops a trailing province, so "Hoàn Kiếm Hà Nội" is looked up as "Hoàn Kiếm".</summary>
    private static string WithoutProvince(string words, string? province) =>
        province is not null && AgentIntakePolicy.Plain(province) is var name && words.EndsWith($" {name}", StringComparison.Ordinal) ? words[..^(name.Length + 1)] : words;
}
