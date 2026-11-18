using System.Linq.Expressions;
using ANGI.Application.Common.Models;
using ANGI.Application.Common.Interfaces.Repositories.Discovery;
using ANGI.Application.Common.Models.Discovery;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ANGI.Infrastructure.Persistences.Repositories.Discovery;

/// <summary>Reads public dishes for the Discovery endpoints.</summary>
public sealed class DishRepository : IDishRepository
{
    private readonly ANGIContext _context;

    public DishRepository(ANGIContext context)
    {
        _context = context;
    }

    /// <inheritdoc />
    public Task<Dish?> GetPublicDetailAsync(int dishId, CancellationToken ct)
    {
        // Same visibility as REST-02: verified and visible; the soft-delete filter hides deleted restaurants.
        return _context.Dishes
            .AsNoTracking()
            .Where(dish => dish.Id == dishId &&
                           dish.Status == DishStatus.Active &&
                           dish.Restaurant.VerificationStatus == RestaurantVerificationStatus.Verified &&
                           dish.Restaurant.ModerationStatus == RestaurantModerationStatus.Visible)
            .Include(dish => dish.CoverMedia)
            .Include(dish => dish.DishTags)
                .ThenInclude(dishTag => dishTag.Tag)
            .Include(dish => dish.Images)
                .ThenInclude(image => image.Media)
            .Include(dish => dish.Restaurant)
                .ThenInclude(restaurant => restaurant.CoverMedia)
            .Include(dish => dish.Restaurant)
                .ThenInclude(restaurant => restaurant.BusinessHours)
            .AsSplitQuery()
            .FirstOrDefaultAsync(ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<int>> GetCandidateIdsAsync(
        DishCandidateFilter filter,
        int maxCount,
        CancellationToken ct)
    {
        // HÀM LẤY DANH SÁCH ỨNG VIÊN: gọi FilteredAsync để có điều kiện lọc, rồi sắp xếp, cắt số lượng và chạy truy vấn, trả về ID món.
        // FilteredAsync trả null khi có tag không tồn tại: không món nào đạt nên trả danh sách rỗng.
        if (await FilteredAsync(filter, ct) is not { } query) return [];
        // Sắp xếp, không loại món nào: rating quán cao trước, rồi nhiều lượt thích, rồi ID nhỏ trước để thứ tự luôn ổn định.
        var ordered = query
            .OrderByDescending(dish => dish.Restaurant.RatingAvg)
            .ThenByDescending(dish => dish.LikeCount)
            .ThenBy(dish => dish.Id);

        // Không giới hạn khu vực: lấy tối đa maxCount ID đầu tiên ngay trong SQL.
        if (filter.Area is null)
        {
            return await ordered.Select(dish => dish.Id).Take(maxCount).ToListAsync(ct);
        }

        // Có khu vực: SQL đã lọc sơ theo ô vuông, tại đây C# tính đúng bán kính rồi mới lấy tối đa maxCount món.
        // The bounding box keeps the rows few; the exact radius is checked here, keeping the order.
        var inBox = await ordered
            .Select(dish => new { dish.Id, dish.Restaurant.Latitude, dish.Restaurant.Longitude })
            .ToListAsync(ct);
        return inBox
            .Where(row => filter.Area.Contains(row.Latitude, row.Longitude))
            .Select(row => row.Id)
            .Take(maxCount)
            .ToArray();
    }

    /// <inheritdoc />
    public async Task<int> CountCandidatesAsync(DishCandidateFilter filter, CancellationToken ct)
    {
        // Đếm số món đạt cùng bộ lọc với GetCandidateIdsAsync, không bị giới hạn bởi maxCount.
        if (await FilteredAsync(filter, ct) is not { } query) return 0;
        if (filter.Area is null) return await query.CountAsync(ct);
        // The exact radius is checked on the rows of the bounding box, as the candidate search does.
        var inBox = await query.Select(dish => new { dish.Restaurant.Latitude, dish.Restaurant.Longitude }).ToListAsync(ct);
        return inBox.Count(row => filter.Area.Contains(row.Latitude, row.Longitude));
    }

    /// <summary>The candidate query with every condition of the filter, the radius as a bounding box; null when a requested tag does not exist.</summary>
    private async Task<IQueryable<Dish>?> FilteredAsync(DishCandidateFilter filter, CancellationToken ct)
    {
        var query = Candidates();
        // HÀM TẠO ĐIỀU KIỆN LỌC: mỗi khối dưới đây thêm một điều kiện AND. Tham số để trống thì bỏ qua khối đó.
        // Hàm này chỉ dựng truy vấn món, chưa đọc danh sách món; truy vấn chạy ở GetCandidateIdsAsync hoặc CountCandidatesAsync.
        // Lọc theo ID: chỉ xét các món được chỉ định, ví dụ món người dùng gọi tên.
        if (filter.DishIds.Count > 0)
        {
            var includedDishes = filter.DishIds.ToArray();
            query = query.Where(dish => includedDishes.Contains(dish.Id));
        }

        // Từ khóa: tên món hoặc tên quán chứa từ khóa, không phân biệt hoa thường.
        if (!string.IsNullOrWhiteSpace(filter.Query))
        {
            var term = LikePattern.Contains(filter.Query);
            query = query.Where(dish => EF.Functions.ILike(dish.Name, term, LikePattern.Escape) ||
                                        EF.Functions.ILike(dish.Restaurant.Name, term, LikePattern.Escape));
        }

        // Tỉnh thành: lọc theo tỉnh của quán; quán chưa có tỉnh sẽ bị loại.
        if (!string.IsNullOrWhiteSpace(filter.Province))
        {
            var term = ProvinceSearch.Pattern(filter.Province);
            query = query.Where(dish => dish.Restaurant.ProvinceName != null && EF.Functions.ILike(dish.Restaurant.ProvinceName, term, LikePattern.Escape));
        }


        // Dạng món: chỉ lấy món đúng dạng được yêu cầu, ví dụ khô hoặc nước.
        if (filter.DishForm is { } dishForm)
        {
            query = query.Where(dish => dish.DishForm == dishForm);
        }

        // Giá tối thiểu: giá món phải lớn hơn hoặc bằng mức này.
        if (filter.MinPrice is { } minPrice)
        {
            query = query.Where(dish => dish.Price >= minPrice);
        }

        // Giá tối đa: giá món phải nhỏ hơn hoặc bằng mức này.
        if (filter.MaxPrice is { } maxPrice)
        {
            query = query.Where(dish => dish.Price <= maxPrice);
        }

        // Nhà hàng: chỉ lấy món của một quán.
        if (filter.RestaurantId is { } restaurantId)
        {
            query = query.Where(dish => dish.RestaurantId == restaurantId);
        }

        // Tag loại trừ: món có bất kỳ tag nào trong danh sách đều bị loại, ví dụ tag dị ứng.
        if (filter.ExcludeTagIds.Count > 0)
        {
            var excluded = filter.ExcludeTagIds.ToArray();
            query = query.Where(dish => !dish.DishTags.Any(dishTag => excluded.Contains(dishTag.TagId)));
        }

        // Một trong các tag: món chỉ cần có ít nhất một tag trong danh sách, không xét loại tag.
        if (filter.AnyTagIds.Count > 0)
        {
            var any = filter.AnyTagIds.ToArray();
            query = query.Where(dish => dish.DishTags.Any(dishTag => any.Contains(dishTag.TagId)));
        }

        // Tag bữa, lọc chặt: món bắt buộc phải có đúng tag bữa này.
        if (!string.IsNullOrWhiteSpace(filter.MealTimeCode))
        {
            var code = filter.MealTimeCode;
            query = query.Where(dish => dish.DishTags.Any(dishTag =>
                dishTag.Tag.TagType == TagType.MealTime && dishTag.Tag.Code == code));
        }
        // Tag bữa, lọc mềm (Agent dùng): món có tag bữa này, hoặc chưa gắn tag bữa nào.
        if (!string.IsNullOrWhiteSpace(filter.CompatibleMealTimeCode))
        {
            var code = filter.CompatibleMealTimeCode;
            query = query.Where(dish => !dish.DishTags.Any(tag => tag.Tag.TagType == TagType.MealTime) ||
                dish.DishTags.Any(tag => tag.Tag.TagType == TagType.MealTime && tag.Tag.Code == code));
        }

        // Giờ mở cửa: OpenAtTime ("bây giờ") được ưu tiên, quán phải đang mở và còn mở ít nhất 30 phút nữa (MealHours.StaysOpen).
        // Nếu không có OpenAtTime thì quán chỉ cần có giờ mở giao với khung giờ của bữa OpenForMeal.
        // OpenOnDays có nhiều thứ thì quán phải đạt ở tất cả các thứ đó; để trống thì chỉ cần đạt ở một thứ bất kỳ.
        // Quán chưa có dữ liệu giờ vẫn được giữ lại (xem OpenDuring).
        IEnumerable<short?> days = filter.OpenOnDays.Count > 0 ? filter.OpenOnDays.Distinct().Select(day => (short?)day) : [null];
        IEnumerable<(MealHours.Window Window, short? Day)> hourChecks = filter.OpenAtTime is { } moment
            ? days.SelectMany(day => MealHours.StaysOpen(moment, day))
            : MealHours.For(filter.OpenForMeal) is { } mealWindow ? days.Select(day => (mealWindow, day)) : [];
        foreach (var (window, day) in hourChecks) query = query.Where(OpenDuring(window, day));

        // Món loại trừ: bỏ các món có ID này, ví dụ món đã gợi ý hoặc đã có trong lịch.
        if (filter.ExcludeDishIds.Count > 0)
        {
            var excludedDishes = filter.ExcludeDishIds.ToArray();
            query = query.Where(dish => !excludedDishes.Contains(dish.Id));
        }

        // Tag yêu cầu: trước hết kiểm tra mọi tag có tồn tại; thiếu tag nào thì không món nào đạt.
        if (filter.TagIds.Count > 0)
        {
            var requested = filter.TagIds.Distinct().ToArray();
            var known = await _context.Tags
                .AsNoTracking()
                .Where(tag => requested.Contains(tag.Id))
                .Select(tag => new { tag.Id, tag.TagType })
                .ToListAsync(ct);
            if (known.Count < requested.Length)
            {
                // A tag that does not exist can match no dish.
                return null;
            }

            // RequireAllTags = true: món phải có đủ từng tag. false: cùng loại tag có một là đủ, khác loại thì phải đạt tất cả.
            // OR within a tag type, AND across types: [lunch, dinner, soup] = (lunch or dinner) and soup.
            var groups = filter.RequireAllTags ? known.Select(tag => new[] { tag.Id }) :
                known.GroupBy(tag => tag.TagType).Select(group => group.Select(tag => tag.Id).ToArray());
            foreach (var ids in groups)
            {
                query = query.Where(dish => dish.DishTags.Any(dishTag => ids.Contains(dishTag.TagId)));
            }
        }

        // Nhóm tag thay thế: mỗi nhóm phải có ít nhất một tag, ví dụ [[cơm, bún], [bò]] = (cơm hoặc bún) và bò.
        foreach (var group in filter.TagAnyGroups)
        {
            var ids = group.ToArray();
            query = query.Where(dish => dish.DishTags.Any(dishTag => ids.Contains(dishTag.TagId)));
        }

        // Khu vực: SQL lọc sơ quán nằm trong ô vuông bao quanh bán kính; bán kính chính xác được tính ở hàm gọi.
        if (filter.Area is { } area)
        {
            var (minLatitude, maxLatitude, minLongitude, maxLongitude) = area.BoundingBox();
            query = query.Where(dish => dish.Restaurant.Latitude >= minLatitude &&
                                        dish.Restaurant.Latitude <= maxLatitude &&
                                        dish.Restaurant.Longitude >= minLongitude &&
                                        dish.Restaurant.Longitude <= maxLongitude);
        }

        return query;
    }

    /// <inheritdoc />
    public void AddRoll(DishRoll roll) => _context.DishRolls.Add(roll);

    /// <inheritdoc />
    public async Task<IReadOnlyList<int>> GetTrendingIdsAsync(string? province, DateTime since, int limit, CancellationToken ct)
    {
        var query = Candidates();
        if (!string.IsNullOrWhiteSpace(province))
        {
            var term = ProvinceSearch.Pattern(province);
            query = query.Where(dish => dish.Restaurant.ProvinceName != null && EF.Functions.ILike(dish.Restaurant.ProvinceName, term, LikePattern.Escape));
        }

        return await query
            .Select(dish => new
            {
                dish.Id,
                RecentLikes = _context.DishFeedbacks.Count(feedback =>
                    feedback.DishId == dish.Id && feedback.Value == FeedbackValue.Like && feedback.CreatedAt >= since),
                dish.LikeCount,
                dish.Restaurant.RatingAvg
            })
            .OrderByDescending(row => row.RecentLikes)
            .ThenByDescending(row => row.LikeCount)
            .ThenByDescending(row => row.RatingAvg)
            .ThenBy(row => row.Id)
            .Select(row => row.Id)
            .Take(limit)
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Dish>> GetCardDishesAsync(IReadOnlyCollection<int> dishIds, CancellationToken ct)
    {
        var ids = dishIds.ToArray();
        return await Candidates()
            .Where(dish => ids.Contains(dish.Id))
            .Include(dish => dish.CoverMedia)
            .Include(dish => dish.Restaurant)
                .ThenInclude(restaurant => restaurant.BusinessHours)
            .Include(dish => dish.DishTags)
                .ThenInclude(dishTag => dishTag.Tag)
            .AsSplitQuery()
            .ToListAsync(ct);
    }

    /// <summary>dish =&gt; !dish.Restaurant.BusinessHours.Any() || dish.Restaurant.BusinessHours.Any(MealHours.Overlaps(window, day)), built so SQL runs the shared rule.</summary>
    // Điều kiện giờ của một thứ: quán không có dòng giờ nào, HOẶC có một dòng giờ giao với khung giờ cần xét (luật ở MealHours.Overlaps).
    private static Expression<Func<Dish, bool>> OpenDuring(MealHours.Window window, short? day)
    {
        var dish = Expression.Parameter(typeof(Dish), "dish");
        var hours = Expression.Property(Expression.Property(dish, nameof(Dish.Restaurant)), nameof(Dish.Restaurant.BusinessHours));
        var none = Expression.Not(Expression.Call(typeof(Enumerable), nameof(Enumerable.Any), [typeof(RestaurantBusinessHour)], hours));
        var reaches = Expression.Call(typeof(Enumerable), nameof(Enumerable.Any), [typeof(RestaurantBusinessHour)], hours, MealHours.Overlaps(window, day));
        return Expression.Lambda<Func<Dish, bool>>(Expression.OrElse(none, reaches), dish);
    }

    /// <summary>
    /// Active, available dishes of open restaurants that are verified and visible; the soft-delete filter hides
    /// deleted restaurants (API Design DISH-01, OWN-05).
    /// </summary>
    // Điều kiện nền của mọi lần lọc: món đang hoạt động và còn bán; quán đã xác minh, đang hiển thị và đang mở.
    private IQueryable<Dish> Candidates() =>
        _context.Dishes
            .AsNoTracking()
            .Where(dish => dish.Status == DishStatus.Active &&
                           dish.IsAvailable &&
                           dish.Restaurant.VerificationStatus == RestaurantVerificationStatus.Verified &&
                           dish.Restaurant.ModerationStatus == RestaurantModerationStatus.Visible &&
                           dish.Restaurant.OperatingStatus == RestaurantOperatingStatus.Open);
}
