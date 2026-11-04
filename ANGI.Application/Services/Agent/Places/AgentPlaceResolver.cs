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
        if (stops.Length == 0 && areaName is null) return null;
        var restaurants = await catalog.GetPublicPlacesAsync(ct);
        var province = state.Province ?? AgentIntakePolicy.FindProvince(AgentIntakePolicy.Plain(content), provinces);

        if (stops.Length == 0)
        {
            if (IsProvince(areaName!, provinces)) return null;
            var area = await ResolveAsync(areaName!, province, planning || bare?.Searchable == true, restaurants, ct);
            return area is null ? null : new(area.Province, [], area, [], [], 0);
        }

        // Xác định tỉnh trước từ các nơi có trong địa chỉ quán, để kết quả không phụ thuộc thứ tự: "Ngày 1 Hòa Lạc, ngày 2 Hoàn Kiếm" cũng tra Hòa Lạc trong Hà Nội.
        foreach (var stop in stops.Where(stop => province is null && !IsProvince(stop.Place, provinces)))
            if (await ResolveAsync(stop.Place, null, false, restaurants, ct) is { } local) { province = local.Province; break; }
        var dayAreas = new List<AgentDayArea>();
        var wholeProvince = new List<short>();
        var unresolved = new List<string>();
        foreach (var stop in stops)
        {
            if (IsProvince(stop.Place, provinces) || AgentItineraryPolicy.IsOpenDay(stop.Place))
            {
                province ??= AgentIntakePolicy.FindProvince(AgentIntakePolicy.Plain(stop.Place), provinces);
                wholeProvince.AddRange(stop.Days);
                continue;
            }
            var match = await ResolveAsync(stop.Place, province, stop.Explicit, restaurants, ct);
            if (match is null)
            {
                // "ngày 1 cay nhẹ" không có động từ đi lại và không phải địa điểm: để luồng thường đọc, không báo là địa danh lạ.
                if (stop.Explicit) unresolved.Add(stop.Place);
                continue;
            }
            province ??= match.Province;
            dayAreas.AddRange(stop.Days.Select(day => new AgentDayArea(day, match.Label, match.Latitude, match.Longitude, match.RadiusKm, match.District)));
        }
        if (dayAreas.Count == 0 && wholeProvince.Count == 0 && unresolved.Count == 0) return null;
        // Mỗi ngày một nơi: tên nêu sau cùng là tên người dùng chốt.
        var byDay = dayAreas.GroupBy(x => x.DayNumber).Select(group => group.Last()).ToArray();
        // Ngày có nơi đến chưa tìm được vẫn thuộc chuyến đi: người dùng đã nêu ngày đó, chỉ là chọn quán trong toàn tỉnh.
        var lastDay = stops.Where(stop => stop.Explicit || stop.Days.Any(day => byDay.Any(x => x.DayNumber == day) || wholeProvince.Contains(day)))
            .SelectMany(stop => stop.Days).DefaultIfEmpty((short)0).Max();
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
        var term = WithoutProvince(AgentIntakePolicy.Plain(place), province);
        if (term.Length < MinPlaceLength || AgentItineraryPolicy.IsOpenDay(place)) return null;
        var inProvince = province is null ? restaurants : restaurants.Where(x => string.Equals(x.Province.Trim(), province.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        var label = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(place.Trim().ToLowerInvariant());
        if (Landmarks.TryGetValue(term, out var landmark)) return Sized(label, landmark.Latitude, landmark.Longitude, inProvince);
        // "phố Huế" là con đường ở Hà Nội; "Huế" là thành phố khác.
        var lowered = place.Trim().ToLowerInvariant();
        var street = StreetWords.FirstOrDefault(word => lowered.StartsWith(word, StringComparison.Ordinal));
        if (street is null && Elsewhere.Contains(term.StartsWith("thanh pho ", StringComparison.Ordinal) ? term["thanh pho ".Length..] : term.StartsWith("tinh ", StringComparison.Ordinal) ? term["tinh ".Length..] : term)) return null;
        var addressed = AddressMatches(term, inProvince);
        if (addressed.Length == 0 && street is not null) addressed = AddressMatches(AgentIntakePolicy.Plain(lowered[street.Length..]), inProvince);
        if (addressed.Length > 0)
        {
            var latitude = Median(addressed.Select(x => x.Latitude));
            var longitude = Median(addressed.Select(x => x.Longitude));
            return Sized(label, latitude, longitude, inProvince);
        }
        if (!searchMap || search is null) return null;
        var nearLatitude = inProvince.Length > 0 ? Median(inProvince.Select(x => x.Latitude)) : (decimal?)null;
        var nearLongitude = inProvince.Length > 0 ? Median(inProvince.Select(x => x.Longitude)) : (decimal?)null;
        // ANGI chỉ có dữ liệu ở một tỉnh thì tra trong tỉnh đó: "Hòa Lạc, Việt Nam" có thể ra nơi trùng tên ở tỉnh khác.
        var catalogProvinces = restaurants.Select(x => x.Province.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToArray();
        var searchIn = province ?? (catalogProvinces.Length == 1 ? catalogProvinces[0] : "Việt Nam");
        var candidates = await search.SearchAsync($"{place.Trim()}, {searchIn}", nearLatitude, nearLongitude, ct);
        // Bản đồ luôn trả về thứ gì đó; chỉ tin khi tên hoặc địa chỉ chứa nguyên cụm người dùng viết.
        return Verified(candidates, term, province) is { } found ? Sized(label, found.Latitude, found.Longitude, inProvince) : null;
    }

    /// <summary>
    /// Trusts a map answer only with evidence, because a business can carry any place's name ("Hồ Gươm Plaza" is in Hà Đông). An area or street containing the
    /// phrase counts, as does a business named exactly that; otherwise most businesses with the phrase in their name must sit together, like the shops of Times City.
    /// </summary>
    private static (decimal Latitude, decimal Longitude)? Verified(AgentPlaceCandidate[] candidates, string term, string? province)
    {
        var usable = candidates.Where(x => !TooBroad.Contains(x.Type ?? "")).ToArray();
        if (usable.FirstOrDefault(x => !IsBusiness(x) && $" {AgentIntakePolicy.Plain($"{x.Name} {x.Address}")} ".Contains($" {term} ", StringComparison.Ordinal)) is { } area)
            return (area.Latitude, area.Longitude);
        var named = usable.Where(x => IsBusiness(x) && $" {AgentIntakePolicy.Plain(x.Name)} ".Contains($" {term} ", StringComparison.Ordinal)).ToArray();
        if (named.FirstOrDefault(x => WithoutProvince(AgentIntakePolicy.Plain(x.Name), province) == term) is { } exact) return (exact.Latitude, exact.Longitude);
        var cluster = named.Select(x => named.Where(other => NearbyArea.DistanceKm(x.Latitude, x.Longitude, other.Latitude, other.Longitude) <= SamePlaceKm).ToArray())
            .MaxBy(group => group.Length);
        return cluster is { Length: >= 2 } && cluster.Length * 2 > named.Length ? (Median(cluster.Select(x => x.Latitude)), Median(cluster.Select(x => x.Longitude))) : null;
    }

    private static bool IsBusiness(AgentPlaceCandidate candidate) => string.Equals(candidate.Type, "poi", StringComparison.OrdinalIgnoreCase);

    /// <summary>Picks the smallest radius holding enough restaurants; null when none lies within the largest radius, whatever the map says.</summary>
    private static AgentPlaceMatch? Sized(string label, decimal latitude, decimal longitude, AgentCatalogPlace[] restaurants)
    {
        var distances = restaurants.Select(x => (x.Province, x.Address, Km: NearbyArea.DistanceKm(latitude, longitude, x.Latitude, x.Longitude))).OrderBy(x => x.Km).ToArray();
        var largest = RadiusSteps[^1];
        if (distances.Length == 0 || distances[0].Km > (double)largest) return null;
        var radius = RadiusSteps.FirstOrDefault(step => distances.Count(x => x.Km <= (double)step) >= EnoughRestaurants, largest);
        return new(distances[0].Province, label, decimal.Round(latitude, 6), decimal.Round(longitude, 6), radius,
            radius > AgentItineraryPolicy.WideRadiusKm ? null : District(distances[0].Address, label));
    }

    /// <summary>The district of the nearest restaurant, from the second to last address part; null when it would only repeat the place's own name.</summary>
    private static string? District(string? address, string label)
    {
        var parts = (address ?? "").Split(',');
        if (parts.Length < 2) return null;
        var district = parts[^2].Trim();
        return district.Length == 0 || AgentIntakePolicy.Plain(district) == AgentIntakePolicy.Plain(label) ? null : district;
    }

    /// <summary>Prefers a whole address part ("..., Hoàn Kiếm, Hà Nội"); a name found inside a longer part, such as a street, is the fallback.</summary>
    private static AgentCatalogPlace[] AddressMatches(string term, AgentCatalogPlace[] restaurants)
    {
        var addressed = restaurants.Where(x => !string.IsNullOrWhiteSpace(x.Address)).Select(x => (Place: x, Parts: x.Address!.Split(',').Select(AddressPart).ToArray())).ToArray();
        var whole = addressed.Where(x => x.Parts.Contains(term)).Select(x => x.Place).ToArray();
        return whole.Length > 0 ? whole : addressed.Where(x => x.Parts.Any(part => $" {part} ".Contains($" {term} ", StringComparison.Ordinal))).Select(x => x.Place).ToArray();
    }

    private static string AddressPart(string part)
    {
        var words = AgentIntakePolicy.Plain(part);
        foreach (var prefix in new[] { "p ", "q ", "x ", "tt ", "phuong ", "quan ", "xa ", "huyen ", "thi tran " })
            if (words.StartsWith(prefix, StringComparison.Ordinal)) return words[prefix.Length..];
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
