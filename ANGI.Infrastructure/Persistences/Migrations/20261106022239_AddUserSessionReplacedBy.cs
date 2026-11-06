using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ANGI.Infrastructure.Persistences.Migrations
{
    /// <inheritdoc />
    public partial class AddUserSessionReplacedBy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "replaced_by_session_id",
                schema: "core",
                table: "user_sessions",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_user_sessions_replaced_by_session_id",
                schema: "core",
                table: "user_sessions",
                column: "replaced_by_session_id");

            migrationBuilder.AddForeignKey(
                name: "fk_user_sessions_user_sessions_replaced_by_session_id",
                schema: "core",
                table: "user_sessions",
                column: "replaced_by_session_id",
                principalSchema: "core",
                principalTable: "user_sessions",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_user_sessions_user_sessions_replaced_by_session_id",
                schema: "core",
                table: "user_sessions");

            migrationBuilder.DropIndex(
                name: "ix_user_sessions_replaced_by_session_id",
                schema: "core",
                table: "user_sessions");

            migrationBuilder.DropColumn(
                name: "replaced_by_session_id",
                schema: "core",
                table: "user_sessions");
        }
    }
}
