namespace ANGI.Domain.Enums
{
    /// <summary>
    /// Tag groups of the dish catalogue, stored as their snake_case names. The numbers follow the catalogue design
    /// (4 Taste and 6 Cuisine were never created) and only fix the API order: tags sort by this value.
    /// </summary>
    public enum TagType
    {
        MealTime = 0,
        Dietary = 1,
        Category = 2,
        MealRole = 3,
        CookingMethod = 5,
        Ingredient = 7
    }
}
