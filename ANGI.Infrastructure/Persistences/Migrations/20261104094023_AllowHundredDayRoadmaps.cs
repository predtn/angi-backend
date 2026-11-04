using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ANGI.Infrastructure.Persistences.Migrations
{
    /// <inheritdoc />
    public partial class AllowHundredDayRoadmaps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_roadmaps_num_days",
                schema: "core",
                table: "roadmaps");

            migrationBuilder.AddCheckConstraint(
                name: "ck_roadmaps_num_days",
                schema: "core",
                table: "roadmaps",
                sql: "num_days BETWEEN 1 AND 100");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_roadmaps_num_days",
                schema: "core",
                table: "roadmaps");

            migrationBuilder.AddCheckConstraint(
                name: "ck_roadmaps_num_days",
                schema: "core",
                table: "roadmaps",
                sql: "num_days BETWEEN 1 AND 30");
        }
    }
}
