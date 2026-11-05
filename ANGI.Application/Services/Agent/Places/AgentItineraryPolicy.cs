using System.Text;
using System.Text.RegularExpressions;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Services.Agent.Conversation;
using ANGI.Application.Services.Agent.Intake;
using ANGI.Application.UseCases.Validators.Agent;

namespace ANGI.Application.Services.Agent.Places;

/// <summary>
/// Reads where the traveler will be from their own words, without a provider call: "Ngày 1 tôi ở Hoàn Kiếm, ngày 2 đi Lăng Bác", or one area for
/// the whole trip such as "ở Cầu Giấy". Whether a name is a real place is decided later by <see cref="AgentPlaceResolver"/>.
/// </summary>
public static class AgentItineraryPolicy
{
    /// <summary>Above this radius the place has few restaurants nearby, and the reply says how far the search reaches.</summary>
    public const decimal WideRadiusKm = 12m;
    private const int MaxPlaceLength = 100;
    private const int MaxBareAnswerWords = 6;
    private const int MaxDay = AgentStateValidator.MaxDays;

    // "ngày 1", "ngày 1 - 3", "ngày 1 và 2", "ngày 1, ngày 2". Không khớp "3 ngày 1 triệu", "bữa tối ngày 1" và ngày tháng "ngày 15/11".
    private static readonly Regex DayHeader = new(
        @"(?<!\bbua\s+\w+\s+)(?<!\d\s*)\bngay\s*(?<from>\d{1,3})(?!\d|[/.]\d)" +
        @"(?:\s*(?<joiner>-|–|den|toi|va|,|&)\s*(?:ngay\s*)?(?<to>\d{1,3})\b(?!\s*(?:ngay|k\b|tr\b|trieu|nghin|ngan)))?\s*(?<colon>[:\-–])?\s*", RegexOptions.CultureInvariant);
    // Các từ đứng trước tên nơi đến; "quan/phuong" là quận, phường viết trước tên riêng. "phố/đường" được giữ lại vì "phố Huế" khác "Huế".
    private static readonly Regex Lead = new(
        @"^\s*(?:(?:toi|minh|em|chung toi|ca nha|gia dinh|ban be|se|dinh|muon|can|phai|doi|chuyen|sang|di choi|di|den|o|tai|tham quan|tham|ghe|qua|dao|choi|quanh|khu vuc|khu|vao|ra|ve|gan|lam viec|hoc|nghi|quan|huyen|phuong|thi xa)\s+)+",
        RegexOptions.CultureInvariant);
    // "đổi", "sang" không tính: "ngày 2 đổi sang bún chả" có thể là tên món, nên chỉ tin khi địa chỉ quán có đúng tên đó, không hỏi bản đồ.
    private static readonly Regex GoingWords = new(@"\b(?:o|tai|di|den|tham|ghe|qua|dao|choi|quanh|khu|vao|ra|ve|gan|lam viec|hoc|nghi)\b", RegexOptions.CultureInvariant);
    // Phần sau tên nơi đến ("Hoàn Kiếm ăn phở", "Cầu Giấy nhé", "Hà Nội 3 ngày").
    private static readonly Regex Tail = new(@"\s+(?:(?:an|uong|va|roi|nhe|nha|thoi|di|giup|dum|luon|sau do|hay|hoac|de|voi|cho|lap|len lich|ngan sach|o|nhung|sao)(?:\s.*|$)|\d.*)", RegexOptions.CultureInvariant);
    // Người dùng gõ có dấu thì so trên chữ có dấu: bỏ dấu sẽ cắt nhầm "Hội An" ("an"), "Hàng Mã" ("mà"), "Chợ Mơ" ("cho").
    private static readonly Regex AccentedTail = new(@"\s+(?:(?:ăn|uống|và|rồi|nhé|nhe|nha|thôi|đi|giúp|dùm|luôn|sau đó|hay|hoặc|để|với|cho|lập|lên lịch|ngân sách|ở|nhưng|sao|mà)(?:\s.*|$)|\d.*)", RegexOptions.CultureInvariant);
    // Ngày không có nơi cụ thể: chọn quán trong toàn tỉnh. "Tự Do" không phải địa danh, tra bản đồ sẽ ra chỗ bất kỳ ("tủ đồ" bỏ dấu cũng là "tu do").
    private static readonly Regex OpenDay = new(@"^(?:tu do|tuy y|tuy thich|chua biet|chua ro|chua co ke hoach|khong co ke hoach|chua quyet dinh|linh hoat|dau cung duoc|di dau cung duoc|o dau cung duoc|nghi ngoi)$", RegexOptions.CultureInvariant);
    // Người dùng báo sai địa điểm. "Sài Gòn" bỏ dấu cũng có chữ "sai" nên phải loại riêng.
    private static readonly Regex CorrectionWords = new(@"\b(?:sai(?! gon)|nham|sao lai|khong phai|y toi la|lech|khong dung)\b", RegexOptions.CultureInvariant);
    private static readonly Regex DishWords = new(@"\b(?:bua|mon)\b", RegexOptions.CultureInvariant);
    // Nơi đúng trong câu báo sai. "đề xuất ở Hà Đông", "lại ở Hà Đông" là nơi người dùng đang chê, không phải nơi họ muốn.
    private static readonly Regex CorrectedPlace = new(@"(?:^|[\s,])(?<!(?:de xuat|goi y|lai|ra|cho)\s)(?:o|tai|di|den|tham quan|quanh|khu vuc|khu|la)\s+(?<place>[^,;.!?\n]+)", RegexOptions.CultureInvariant);
    // Cả câu chỉ nêu một nơi mà không nêu ngày: "ở Hoàn Kiếm", "tôi đi Lăng Bác", "đi ăn ở Tây Hồ", "khu Cầu Giấy".
    private static readonly Regex GoingArea = new(@"^\s*(?:(?:toi|minh|em)\s+)?(?:(?:se|dinh|muon)\s+)*(?:(?:di\s+)?an\s+)?(?:o|tai|di|den|quanh|ghe|tham quan|khu vuc|khu)\s+(?<place>[^,;\n]+?)[.!?\s]*$", RegexOptions.CultureInvariant);
    // "đổi sang khu Cầu Giấy", "đổi hành trình sang Hoàn Kiếm". "đổi sang Times City" không có chữ "khu" hay "hành trình" có thể là tên món nên chỉ xét địa chỉ quán.
    private static readonly Regex ChangeArea = new(@"^\s*(?:(?:toi|minh|em)\s+)?(?:muon\s+)?(?:doi|chuyen)\s+(?:(?<trip>hanh trinh|lich trinh|lich|ke hoach|lo trinh|diem den|noi den)\s+)?(?:sang|qua|den|toi|ve)\s+(?<place>[^,;\n]+?)[.!?\s]*$", RegexOptions.CultureInvariant);
    private static readonly Regex WholeTripAnswer = new(@"^(?:(?:doi|chon)\s+)?(?:ca chuyen|toan chuyen|ca lich|ca hanh trinh|tat ca|moi ngay|toan bo|ca \d+ ngay|ca (?:hai|ba|bon|nam) ngay)(?:\s+(?:nhe|nha|luon|di|cac ngay))*$", RegexOptions.CultureInvariant);
    private static readonly Regex BareDays = new(@"^\d{1,3}(?:\s*(?:,|va|&|-|den|toi)\s*\d{1,3})?$", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> AnswerFillers = ["nhe", "nha", "thoi", "a", "luon", "di", "doi", "chon", "chi", "cho", "toi", "minh", "voi"];
    // Vế bắt đầu bằng những từ này nói về món hoặc khẩu vị của ngày, không phải nơi đến.
    private static readonly Regex NotPlace = new(@"^(?:\d|(?:an|uong|do uong|mon|bua|chi|theo|khong|thoai mai|tuy|cay|ngot|man|chua|dang|ngan sach)\b)", RegexOptions.CultureInvariant);
    private static readonly Regex AreaPhrase = new(@"(?:^|[\s,])(?:o|tai|quanh|khu vuc|khu|gan)\s+(?<place>[^,;.!?\n]+)", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> Vague = ["day", "dau", "nha", "toi", "minh", "ban", "vi tri hien tai", "vi tri cua toi", "vi tri cua minh", "cho toi dang o", "noi toi dang o"];

    /// <summary>Finds the day-by-day places in a message, in order; food or taste wording for a day is never a stop.</summary>
    public static AgentItineraryStop[] Stops(string content)
    {
        // STEP-01: Prepare text through content.Normalize, folded through Fold, headers through DayHeader.Matches and stops as List<AgentItineraryStop>.
        var text = content.Normalize(NormalizationForm.FormC);
        var folded = Fold(text);
        var headers = DayHeader.Matches(folded);
        var stops = new List<AgentItineraryStop>();
        // STEP-02: Repeat while index < headers.Count.
        for (var index = 0; index < headers.Count; index++)
        {
            // STEP-03: Prepare header, start and clauseEnd through folded.IndexOfAny.
            var header = headers[index];
            var start = header.Index + header.Length;
            var clauseEnd = folded.IndexOfAny([',', ';', '.', '!', '?', '\n'], start);
            // STEP-04: When clauseEnd < 0, set clauseEnd.
            if (clauseEnd < 0) clauseEnd = folded.Length;
            // STEP-05: When index + 1 < headers.Count, set clauseEnd.
            if (index + 1 < headers.Count) clauseEnd = Math.Min(clauseEnd, headers[index + 1].Index);
            // STEP-06: Prepare days through Days.
            var days = Days(header);
            // STEP-07: Skip this round when days.Length is 0 or Place() is not { } found.
            if (days.Length == 0 || Place(text, folded, start, clauseEnd) is not { } found) continue;
            // STEP-08: Add the built entry to stops.
            stops.Add(new(days, found.Name, header.Groups["colon"].Success || found.Going));
        }
        // STEP-09: Return one of two results depending on stops.Count >= 2.
        // Một danh sách "Ngày 1 A, ngày 2 B" đã rõ là hành trình dù không có động từ nào.
        return stops.Count >= 2 ? stops.Select(stop => stop with { Explicit = true }).ToArray() : stops.ToArray();
    }

    /// <summary>Finds one area for the whole trip ("ở Cầu Giấy"); a short reply to the place question may be the bare name ("Hai Bà Trưng").</summary>
    public static string? TripArea(string content, bool asked)
    {
        // STEP-01: Prepare text through content.Normalize and folded through Fold.
        var text = content.Normalize(NormalizationForm.FormC);
        var folded = Fold(text);
        // STEP-02: When AreaPhrase.Matches().LastOrDefault() exists, run the branch below.
        if (AreaPhrase.Matches(folded).LastOrDefault() is { } phrase)
        {
            // STEP-03: Prepare group, inner through Regex.Matches().LastOrDefault and start.
            var group = phrase.Groups["place"];
            // "ở Hà Nội khu Cầu Giấy": nơi cụ thể nhất là cụm sau từ chỉ nơi cuối cùng.
            var inner = Regex.Matches(group.Value, @"\s(?:o|tai|quanh|khu vuc|khu|gan)\s", RegexOptions.CultureInvariant).LastOrDefault();
            var start = group.Index + (inner is null ? 0 : inner.Index + inner.Length);
            // STEP-04: Return Place().Name.
            return Place(text, folded, start, group.Index + group.Length)?.Name;
        }
        // STEP-05: Return null early when asked is false or folded.Split().Length > MaxBareAnswerWords.
        if (!asked || folded.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length > MaxBareAnswerWords) return null;
        // STEP-06: Prepare end through folded.IndexOfAny.
        var end = folded.IndexOfAny([',', ';', '.', '!', '?', '\n']);
        // STEP-07: Return Place().Name.
        return Place(text, folded, 0, end < 0 ? folded.Length : end)?.Name;
    }

    /// <summary>
    /// Finds a message that only names a place, with no day: "ở Hoàn Kiếm", "đổi sang khu Cầu Giấy". Searchable says the wording clearly means a place,
    /// so a map search may be used; otherwise the name could be a dish and only catalog addresses may confirm it.
    /// </summary>
    public static (string Name, bool Searchable)? BareArea(string content)
    {
        // STEP-01: Prepare text through content.Normalize, folded through Fold, going through GoingArea.Match, change from ChangeArea.Match and 1 more.
        var text = content.Normalize(NormalizationForm.FormC);
        var folded = Fold(text);
        var going = GoingArea.Match(folded);
        var change = going.Success ? Match.Empty : ChangeArea.Match(folded);
        var group = (going.Success ? going : change).Groups["place"];
        // STEP-02: Return null early when group.Success is false or Place() is not { } found.
        if (!group.Success || Place(text, folded, group.Index, group.Index + group.Length) is not { } found) return null;
        // STEP-03: Return the collected values.
        return (found.Name, going.Success || change.Groups["trip"].Success || group.Value.StartsWith("khu", StringComparison.Ordinal));
    }

    /// <summary>Reads the reply to "ngày nào?": "ngày 2", "ngày 1 và 3", "2", "cả chuyến". Empty when the reply is about something else or names no day of the trip.</summary>
    public static short[] AnsweredDays(string content, short numDays)
    {
        // STEP-01: Prepare folded through Fold().Trim().TrimEnd.
        var folded = Fold(content.Normalize(NormalizationForm.FormC)).Trim().TrimEnd('.', '!', '?', ' ');
        // STEP-02: Return the result of Range().Select(filtered).ToArray early when folded matches WholeTripAnswer.
        if (WholeTripAnswer.IsMatch(folded)) return Enumerable.Range(1, numDays).Select(day => (short)day).ToArray();
        // STEP-03: Prepare answer from BareDays.IsMatch and headers through DayHeader.Matches.
        var answer = BareDays.IsMatch(folded) ? $"ngay {folded}" : folded;
        var headers = DayHeader.Matches(answer);
        // STEP-04: Return the collected items early when headers.Count is 0 or DayHeader.Replace().Split().Any matches the filter.
        // "ngày 2 ăn phở" là yêu cầu khác có nhắc ngày, không phải câu trả lời.
        if (headers.Count == 0 || DayHeader.Replace(answer, " ").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Any(word => !AnswerFillers.Contains(word))) return [];
        // STEP-05: Return the result of Where(filtered).Distinct().Order().ToArray.
        return headers.SelectMany(Days).Where(day => day <= numDays).Distinct().Order().ToArray();
    }

    /// <summary>Applies what the message said: days move to their new places, and the trip grows when the itinerary names a later day.</summary>
    public static AgentState Apply(AgentState state, AgentTripPlaces trip)
    {
        // STEP-01: Prepare stated through DayAreas.Select(filtered).Concat().ToHashSet, areas through Concat().OrderBy(filtered).ToArray and more.
        var stated = trip.DayAreas.Select(x => x.DayNumber).Concat(trip.WholeProvinceDays).ToHashSet();
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
        return trip.TripArea is { } area ? applied with { OriginMode = "pinned", OriginLatitude = area.Latitude, OriginLongitude = area.Longitude,
            OriginLabel = area.Label, RadiusKm = area.RadiusKm, NamedAreaLabel = area.Label } : applied;
    }

    /// <summary>Tells the user how the message was understood, so a wrong reading or an unknown place shows before any dish is chosen.</summary>
    public static string? Note(AgentState state, AgentTripPlaces trip)
    {
        // STEP-01: Prepare known, province through AgentReplyBuilder.Safe and parts as List<string>.
        var known = state.Province ?? trip.Province;
        var province = AgentReplyBuilder.Safe(known);
        var parts = new List<string>();
        // STEP-02: When trip.TripArea exists, call parts.Add.
        if (trip.TripArea is { } area)
            parts.Add($"Mình ghi nhận khu vực {AgentReplyBuilder.Safe(area.Label)}{(area.District is { } district ? $" (khu {AgentReplyBuilder.Safe(district)})" : "")}, mình sẽ ưu tiên quán trong khoảng {area.RadiusKm:0} km quanh đó.");
        // STEP-03: When state.DayAreas has the expected shape and trip.DayAreas.Length > 0, call parts.Add.
        if (state.DayAreas is { Length: > 0 } areas && trip.DayAreas.Length > 0)
            parts.Add($"Mình ghi nhận hành trình ở {province}: {string.Join("; ", areas.Select(Describe))}.");
        // STEP-04: When trip.Unresolved.Length > 0, prepare unknown through string.Join and call parts.Add.
        if (trip.Unresolved.Length > 0)
        {
            var unknown = string.Join(", ", trip.Unresolved.Select(place => $"“{AgentReplyBuilder.Safe(place)}”"));
            parts.Add(known is null ? $"Mình chưa tìm được quán quanh {unknown}."
                : $"Mình chưa tìm được quán quanh {unknown} nên phần đó sẽ chọn trong toàn {province}; bạn có thể nhắn lại tên khu khác.");
        }
        // STEP-05: When state.DayAreas has the expected shape, state.NumDays exists and named.Length < total, call parts.Add.
        if (state.DayAreas is { Length: > 0 } named && state.NumDays is { } total && named.Length < total)
            parts.Add($"Những ngày còn lại trong {total} ngày, mình chọn trong toàn {province}.");
        // STEP-06: Return one of two results depending on parts.Count is 0.
        return parts.Count == 0 ? null : string.Join(' ', parts);
    }

    /// <summary>One day of the itinerary for chat; says how far the search reaches when the place has few restaurants nearby.</summary>
    public static string Describe(AgentDayArea area) => $"ngày {area.DayNumber} quanh {AgentReplyBuilder.Safe(area.Label)}" +
        (area.District is { } district ? $" (khu {AgentReplyBuilder.Safe(district)})" : "") +
        (area.RadiusKm > WideRadiusKm ? $" (quanh đó còn ít quán nên mình chọn quán gần nhất, trong khoảng {area.RadiusKm:0} km)" : "");

    /// <summary>True when a day has no fixed place ("đi tự do", "chưa biết"), so its dishes come from the whole province.</summary>
    public static bool IsOpenDay(string place) => OpenDay.IsMatch(Fold(place.Normalize(NormalizationForm.FormC)).Trim());

    /// <summary>
    /// Reads a complaint that the itinerary is in the wrong place ("ngày 1 sai", "ý tôi là Hồ Gươm ở Hoàn Kiếm"). Only a plan that follows named places
    /// qualifies, and wording about a dish or a meal stays with the normal edit flow.
    /// </summary>
    public static AgentPlaceCorrection? Correction(string content, AgentState state)
    {
        // STEP-01: Return null early when state.DayAreas is not { Length: > 0 } areas.
        if (state.DayAreas is not { Length: > 0 } areas) return null;
        // STEP-02: Prepare text through content.Normalize and folded through Fold.
        var text = content.Normalize(NormalizationForm.FormC);
        var folded = Fold(text);
        // STEP-03: Return null early when folded does not match CorrectionWords, folded matches DishWords or text contains a message.
        if (!CorrectionWords.IsMatch(folded) || DishWords.IsMatch(folded) || text.Contains("quán", StringComparison.OrdinalIgnoreCase)) return null;
        // STEP-04: Prepare days through Where(filtered).Distinct().ToArray.
        var days = DayHeader.Matches(folded).SelectMany(Days).Where(day => day <= state.NumDays).Distinct().ToArray();
        // STEP-05: When days.Length is 0, set days.
        if (days.Length == 0) days = areas.Where(area => folded.Contains(Fold(area.Label.Normalize(NormalizationForm.FormC)), StringComparison.Ordinal)).Select(area => area.DayNumber).ToArray();
        // STEP-06: For each phrase in CorrectedPlace.Matches(), prepare group and return the new result early when Place() exists and IsOpenDay(found.Name) does not hold.
        foreach (Match phrase in CorrectedPlace.Matches(folded))
        {
            var group = phrase.Groups["place"];
            if (Place(text, folded, group.Index, group.Index + group.Length) is { } found && !IsOpenDay(found.Name)) return new(days, found.Name);
        }
        // STEP-07: Return the new result.
        return new(days, null);
    }

    /// <summary>Says where the plan is searching and how to fix it, for a complaint the backend cannot resolve alone; costs no provider call and leaves the plan savable.</summary>
    public static string CorrectionHelp(AgentState state, short[] days, string? unknownPlace)
    {
        // STEP-01: Prepare shown through Where(filtered).OrderBy(filtered).ToArray, day from shown.FirstOrDefault, current from AgentReplyBuilder.Safe and more.
        var shown = (state.DayAreas ?? []).Where(area => days.Length == 0 || days.Contains(area.DayNumber)).OrderBy(area => area.DayNumber).ToArray();
        var day = days.Length > 0 ? days[0] : shown.FirstOrDefault()?.DayNumber ?? 1;
        var current = shown.Length > 0 ? $"Hiện mình đang tìm quán: {string.Join("; ", shown.Select(Describe))}. " : $"Ngày {day} hiện đang chọn trong toàn {AgentReplyBuilder.Safe(state.Province)}. ";
        var unknown = unknownPlace is null ? "" : $"Mình chưa định vị được “{AgentReplyBuilder.Safe(unknownPlace)}”. ";
        // STEP-02: Return the formatted text.
        return $"{unknown}{current}Nếu sai địa điểm, bạn nhắn “Ngày {day} ở” kèm tên khu hoặc địa danh đúng, ví dụ “Ngày {day} ở Hoàn Kiếm”. Nếu muốn đổi món, bạn nhắn ví dụ “đổi bữa trưa ngày {day} sang món khác” nhé.";
    }

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

    private static short[] Days(Match header)
    {
        // STEP-01: Return the collected items early when header.Groups["from"].Value does not parse or from is < 1 or > MaxDay.
        if (!short.TryParse(header.Groups["from"].Value, out var from) || from is < 1 or > MaxDay) return [];
        // STEP-02: Return the collected items early when header.Groups["to"].Success is false, header.Groups["to"].Value does not parse or to is < 1 or > MaxDay.
        if (!header.Groups["to"].Success || !short.TryParse(header.Groups["to"].Value, out var to) || to is < 1 or > MaxDay) return [from];
        // STEP-03: Return one of two results depending on from is to early when header.Groups["joiner"].Value is "va" or "," or "&".
        if (header.Groups["joiner"].Value is "va" or "," or "&") return from == to ? [from] : [from, to];
        // STEP-04: Return one of two results depending on to < from.
        return to < from ? [] : Enumerable.Range(from, to - from + 1).Select(day => (short)day).ToArray();
    }

    private static (string Name, bool Going)? Place(string text, string folded, int start, int end)
    {
        // STEP-01: Prepare clause, lead through Lead.Match, bodyStart, body and 2 more.
        var clause = folded[start..end];
        var lead = Lead.Match(clause);
        var bodyStart = lead.Success ? lead.Length : 0;
        var body = clause[bodyStart..];
        var original = text.Substring(start + bodyStart, body.Length).ToLowerInvariant();
        var tail = original == body ? Tail.Match(body) : AccentedTail.Match(original);
        // STEP-02: When tail.Success is true, set body.
        if (tail.Success) body = body[..tail.Index];
        // STEP-03: Prepare name through text.Substring().Trim and plain through body.Trim.
        var name = text.Substring(start + bodyStart, body.Length).Trim();
        var plain = body.Trim();
        // STEP-04: Return the collected values early when plain matches OpenDay.
        if (OpenDay.IsMatch(plain)) return (name, true);
        // STEP-05: Return null early when plain.Length < 2, name.Length > MaxPlaceLength, plain matches NotPlace or 3 more condition(s).
        // "quán" là tên quán, do luồng tìm quán xử lý; không nhầm với "quận" vì so trên chữ gốc có dấu.
        if (plain.Length < 2 || name.Length > MaxPlaceLength || NotPlace.IsMatch(plain) || Vague.Contains(plain) ||
            text.Substring(start, end - start).Contains("quán", StringComparison.OrdinalIgnoreCase) || AgentTasteClarificationPolicy.StatesTaste(name)) return null;
        // STEP-06: Return the collected values.
        return (name, lead.Success && GoingWords.IsMatch(lead.Value));
    }
}
