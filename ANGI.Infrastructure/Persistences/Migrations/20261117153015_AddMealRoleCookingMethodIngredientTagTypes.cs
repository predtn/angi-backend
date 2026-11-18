using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ANGI.Infrastructure.Persistences.Migrations
{
    /// <inheritdoc />
    public partial class AddMealRoleCookingMethodIngredientTagTypes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_tags_tag_type",
                schema: "core",
                table: "tags");

            migrationBuilder.AddCheckConstraint(
                name: "ck_tags_tag_type",
                schema: "core",
                table: "tags",
                sql: "tag_type IN ('meal_time', 'dietary', 'category', 'meal_role', 'cooking_method', 'ingredient')");

            // Data only EF cannot express: move an existing catalogue onto the six types (no-op on an empty catalogue).
            migrationBuilder.Sql(TagCatalogSql.Up);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Rows of the three newer types must be gone before the CHECK narrows again.
            migrationBuilder.Sql(TagCatalogSql.Down);

            migrationBuilder.DropCheckConstraint(
                name: "ck_tags_tag_type",
                schema: "core",
                table: "tags");

            migrationBuilder.AddCheckConstraint(
                name: "ck_tags_tag_type",
                schema: "core",
                table: "tags",
                sql: "tag_type IN ('meal_time', 'dietary', 'category')");
        }
    }
}
