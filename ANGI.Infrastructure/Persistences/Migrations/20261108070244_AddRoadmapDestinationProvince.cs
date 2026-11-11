using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ANGI.Infrastructure.Persistences.Migrations
{
    /// <inheritdoc />
    public partial class AddRoadmapDestinationProvince : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "destination_province",
                schema: "core",
                table: "roadmaps",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "destination_province",
                schema: "core",
                table: "roadmaps");
        }
    }
}
