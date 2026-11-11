using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ANGI.Infrastructure.Persistences.Migrations
{
    /// <inheritdoc />
    public partial class DropRestaurantDistrict : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "district",
                schema: "core",
                table: "restaurants");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "district",
                schema: "core",
                table: "restaurants",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);
        }
    }
}
