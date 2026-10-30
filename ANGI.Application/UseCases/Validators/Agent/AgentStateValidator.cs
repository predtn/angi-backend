using ANGI.Application.Common.Models.Agent;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Agent;

/// <summary>Validates every structured trip value before tools, ready state or commit.</summary>
public sealed class AgentStateValidator : AbstractValidator<AgentState>
{
    public static readonly string[] Meals = ["breakfast", "lunch", "dinner", "snack", "late_night"];
    public const decimal MaxMoney = 99999999999999m;

    /// <summary>Defines bounds, monetary precision, origins and scope relationships.</summary>
    public AgentStateValidator()
    {
        RuleFor(x => x.SchemaVersion).Equal(1).WithMessage("Phiên bản state không hợp lệ.");
        RuleFor(x => x.PreferenceApplication).Must(x => x is "replan" or "future").WithMessage("Cách áp dụng khẩu vị không hợp lệ.");
        RuleFor(x => x.MealSlots).NotNull().Must(x => x is null || x.Length <= 5 && x.All(Meals.Contains) && x.Distinct().Count() == x.Length)
            .WithMessage("Danh sách bữa cần có không hợp lệ.");
        RuleFor(x => x.TasteOverrides).NotNull().Must((state, values) => values is null || values.Length <= 180 &&
            values.All(x => x is not null && ValidScope(state, x.DayNumber, x.MealSlot) && ValidTargets(x.Targets)) &&
            values.Select(x => (x.DayNumber, x.MealSlot)).Distinct().Count() == values.Length)
            .WithMessage("Khẩu vị theo ngày/bữa không hợp lệ hoặc trùng phạm vi.");
        RuleFor(x => x.MealRequests).NotNull().Must((state, values) => values is null || values.Length <= 181 &&
            values.All(x => x is not null && ValidScope(state, x.DayNumber, x.MealSlot) && ValidName(x.DishName) && ValidName(x.RestaurantName) &&
                (!x.RestaurantId.HasValue || x.RestaurantId > 0 && x.RestaurantName is not null) &&
                x.TagIds is not null && x.TagIds.Length <= 100 && x.TagIds.All(id => id > 0) && x.TagIds.Distinct().Count() == x.TagIds.Length) &&
            values.Select(x => (x.DayNumber, x.MealSlot)).Distinct().Count() == values.Length)
            .WithMessage("Yêu cầu món/quán theo phạm vi không hợp lệ.");
        RuleFor(x => x.TasteTargets).NotNull().Must(x => x is null || x.Length <= 12 &&
            x.All(t => t is not null && AgentTasteOntology.Dimensions.Contains(t.Dimension) && AgentTasteOntology.Scale.Contains(t.Target)) &&
            x.Select(t => t.Dimension).Distinct().Count() == x.Length).WithMessage("Mức khẩu vị phải thuộc ontology và không trùng chiều.");
        RuleFor(x => x.Preferences).Must(x => x is null || x.OntologyVersion == 1 &&
            AgentTasteOntology.ValidVector(x.BaselineVector) && AgentTasteOntology.ValidVector(x.EffectiveVector))
            .WithMessage("Vector khẩu vị không hợp lệ.");
        RuleFor(x => x.Province).MaximumLength(100).WithMessage("Tên tỉnh không được vượt quá 100 ký tự.")
            .Must(x => x is null || !string.IsNullOrWhiteSpace(x)).WithMessage("Tỉnh không hợp lệ.");
        RuleFor(x => x.NumDays).InclusiveBetween((short)1, (short)30).WithMessage("Số ngày phải từ 1 đến 30.").When(x => x.NumDays.HasValue);
        RuleFor(x => x.Title).NotEmpty().WithMessage("Vui lòng nhập tên lịch trình.")
            .MaximumLength(200).WithMessage("Tên lịch trình không được vượt quá 200 ký tự.");
        RuleFor(x => x.BudgetAmount).Must(ValidMoney).WithMessage("Ngân sách phải là số nguyên VND không âm.");
        RuleFor(x => x.MaxPricePerMeal).Must(ValidMoney).WithMessage("Giá tối đa phải là số nguyên VND không âm.");
        RuleFor(x => x.TagIds).NotNull().WithMessage("Danh sách tag không được để null.")
            .Must(x => x is null || x.Length <= 100 && x.All(id => id > 0) && x.Distinct().Count() == x.Length)
            .WithMessage("Danh sách tag tối đa 100 ID dương và không trùng nhau.");
        RuleFor(x => x.ExcludeTagIds).NotNull().WithMessage("Danh sách tag loại trừ không được để null.")
            .Must(x => x is null || x.Length <= 100 && x.All(id => id > 0) && x.Distinct().Count() == x.Length)
            .WithMessage("Danh sách tag loại trừ tối đa 100 ID dương và không trùng nhau.");
        RuleFor(x => x.OriginMode).NotEmpty().WithMessage("Vui lòng chọn loại điểm xuất phát.");
        RuleFor(x => x.OriginMode).Must(x => x is "none" or "pinned" or "gps").WithMessage("Loại điểm xuất phát không hợp lệ.")
            .When(x => !string.IsNullOrWhiteSpace(x.OriginMode));
        RuleFor(x => x.OriginLatitude).InclusiveBetween(-90m, 90m).WithMessage("Vĩ độ phải từ -90 đến 90.").When(x => x.OriginLatitude.HasValue);
        RuleFor(x => x.OriginLongitude).InclusiveBetween(-180m, 180m).WithMessage("Kinh độ phải từ -180 đến 180.").When(x => x.OriginLongitude.HasValue);
        RuleFor(x => x).Must(x => x.OriginLatitude.HasValue == x.OriginLongitude.HasValue)
            .WithMessage("Cần gửi đủ hai tọa độ.");
        RuleFor(x => x).Must(x => x.OriginMode != "pinned" || x.OriginLatitude.HasValue)
            .WithMessage("Điểm đã chấm cần tọa độ.");
        RuleFor(x => x).Must(x => x.OriginMode != "none" || !x.OriginLatitude.HasValue && x.OriginLabel is null)
            .WithMessage("Không gửi tọa độ khi originMode là none.");
        RuleFor(x => x.OriginLabel).MaximumLength(200).WithMessage("Tên điểm xuất phát không được vượt quá 200 ký tự.");
        RuleFor(x => x.RadiusKm).InclusiveBetween(1m, 50m).WithMessage("Bán kính phải từ 1 đến 50 km.").When(x => x.RadiusKm.HasValue);
        RuleFor(x => x).Must(x => !x.RadiusKm.HasValue || x.OriginLatitude.HasValue)
            .WithMessage("Bán kính cần một điểm xuất phát.");
        RuleFor(x => x.Note).MaximumLength(500).WithMessage("Ghi chú không được vượt quá 500 ký tự.");
        RuleFor(x => x.PendingQuestion).MaximumLength(1000).WithMessage("Câu hỏi đang chờ không được vượt quá 1000 ký tự.");
        RuleFor(x => x.FocusDay).Must((state, day) => !day.HasValue || day >= 1 && day <= state.NumDays)
            .WithMessage("Ngày đang chọn ngoài phạm vi chuyến đi.");
        RuleFor(x => x.FocusMeal).Must(meal => meal is null || Meals.Contains(meal)).WithMessage("Bữa đang chọn không hợp lệ.");
        RuleFor(x => x).Must(x => x.FocusMeal is null || x.FocusDay.HasValue).WithMessage("Bữa đang chọn cần ngày.");
        RuleFor(x => x.StartDate).Must((state, date) => !date.HasValue || date.Value.DayNumber <= DateOnly.MaxValue.DayNumber - (state.NumDays ?? 1) + 1)
            .WithMessage("Ngày kết thúc vượt giới hạn.");
    }

    /// <summary>Accepts only database-representable nonnegative integer VND amounts.</summary>
    private static bool ValidMoney(decimal? value) => !value.HasValue || value >= 0 && value <= MaxMoney && decimal.Truncate(value.Value) == value;

    private static bool ValidScope(AgentState state, short? day, string? meal) =>
        (!day.HasValue || day >= 1 && day <= state.NumDays) && (meal is null || day.HasValue && Meals.Contains(meal));

    private static bool ValidName(string? name) => name is null || !string.IsNullOrWhiteSpace(name) && name.Length <= 200;

    private static bool ValidTargets(AgentTasteTarget[]? values) => values is not null && values.Length <= 12 &&
        values.All(x => x is not null && AgentTasteOntology.Dimensions.Contains(x.Dimension) && AgentTasteOntology.Scale.Contains(x.Target)) &&
        values.Select(x => x.Dimension).Distinct().Count() == values.Length;
}
