using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ANGI.Infrastructure.Persistences.Migrations
{
    /// <inheritdoc />
    public partial class AddRoadmapOriginAndRequireRestaurantCoordinates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "origin_label",
                schema: "core",
                table: "roadmaps",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "origin_latitude",
                schema: "core",
                table: "roadmaps",
                type: "numeric(9,6)",
                precision: 9,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "origin_longitude",
                schema: "core",
                table: "roadmaps",
                type: "numeric(9,6)",
                precision: 9,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "origin_mode",
                schema: "core",
                table: "roadmaps",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "none");

            // EF adds defaultValue: 0m when a column becomes NOT NULL. Removed on purpose:
            // a restaurant without coordinates must fail, not silently sit at (0, 0).
            migrationBuilder.AlterColumn<decimal>(
                name: "longitude",
                schema: "core",
                table: "restaurants",
                type: "numeric(9,6)",
                precision: 9,
                scale: 6,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(9,6)",
                oldPrecision: 9,
                oldScale: 6,
                oldNullable: true);

            migrationBuilder.AlterColumn<decimal>(
                name: "latitude",
                schema: "core",
                table: "restaurants",
                type: "numeric(9,6)",
                precision: 9,
                scale: 6,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(9,6)",
                oldPrecision: 9,
                oldScale: 6,
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_roadmaps_origin",
                schema: "core",
                table: "roadmaps",
                sql: "(origin_mode = 'pinned' AND origin_latitude IS NOT NULL AND origin_longitude IS NOT NULL) OR (origin_mode <> 'pinned' AND origin_latitude IS NULL AND origin_longitude IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_roadmaps_origin_mode",
                schema: "core",
                table: "roadmaps",
                sql: "origin_mode IN ('none', 'pinned', 'gps')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_roadmaps_origin",
                schema: "core",
                table: "roadmaps");

            migrationBuilder.DropCheckConstraint(
                name: "ck_roadmaps_origin_mode",
                schema: "core",
                table: "roadmaps");

            migrationBuilder.DropColumn(
                name: "origin_label",
                schema: "core",
                table: "roadmaps");

            migrationBuilder.DropColumn(
                name: "origin_latitude",
                schema: "core",
                table: "roadmaps");

            migrationBuilder.DropColumn(
                name: "origin_longitude",
                schema: "core",
                table: "roadmaps");

            migrationBuilder.DropColumn(
                name: "origin_mode",
                schema: "core",
                table: "roadmaps");

            migrationBuilder.AlterColumn<decimal>(
                name: "longitude",
                schema: "core",
                table: "restaurants",
                type: "numeric(9,6)",
                precision: 9,
                scale: 6,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "numeric(9,6)",
                oldPrecision: 9,
                oldScale: 6);

            migrationBuilder.AlterColumn<decimal>(
                name: "latitude",
                schema: "core",
                table: "restaurants",
                type: "numeric(9,6)",
                precision: 9,
                scale: 6,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "numeric(9,6)",
                oldPrecision: 9,
                oldScale: 6);
        }
    }
}
