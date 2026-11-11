using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ANGI.Infrastructure.Persistences.Migrations
{
    /// <inheritdoc />
    public partial class AddNameTrigramIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:citext", ",,")
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:citext", ",,");

            migrationBuilder.CreateIndex(
                name: "ix_restaurants_name_trgm",
                schema: "core",
                table: "restaurants",
                column: "name")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_dishes_name_trgm",
                schema: "core",
                table: "dishes",
                column: "name")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_restaurants_name_trgm",
                schema: "core",
                table: "restaurants");

            migrationBuilder.DropIndex(
                name: "ix_dishes_name_trgm",
                schema: "core",
                table: "dishes");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:citext", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:citext", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:pg_trgm", ",,");
        }
    }
}
