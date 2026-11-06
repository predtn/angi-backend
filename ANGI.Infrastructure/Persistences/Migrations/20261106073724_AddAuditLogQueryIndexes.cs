using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ANGI.Infrastructure.Persistences.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditLogQueryIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_audit_logs_actor_id",
                schema: "core",
                table: "audit_logs");

            migrationBuilder.DropIndex(
                name: "ix_audit_logs_subject_user_id",
                schema: "core",
                table: "audit_logs");

            migrationBuilder.CreateIndex(
                name: "ix_audit_logs_action_created_at",
                schema: "core",
                table: "audit_logs",
                columns: new[] { "action", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_audit_logs_actor_id_created_at",
                schema: "core",
                table: "audit_logs",
                columns: new[] { "actor_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_audit_logs_created_at",
                schema: "core",
                table: "audit_logs",
                column: "created_at",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_audit_logs_entity_type_entity_id",
                schema: "core",
                table: "audit_logs",
                columns: new[] { "entity_type", "entity_id" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_logs_subject_user_id_created_at",
                schema: "core",
                table: "audit_logs",
                columns: new[] { "subject_user_id", "created_at" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_audit_logs_action_created_at",
                schema: "core",
                table: "audit_logs");

            migrationBuilder.DropIndex(
                name: "ix_audit_logs_actor_id_created_at",
                schema: "core",
                table: "audit_logs");

            migrationBuilder.DropIndex(
                name: "ix_audit_logs_created_at",
                schema: "core",
                table: "audit_logs");

            migrationBuilder.DropIndex(
                name: "ix_audit_logs_entity_type_entity_id",
                schema: "core",
                table: "audit_logs");

            migrationBuilder.DropIndex(
                name: "ix_audit_logs_subject_user_id_created_at",
                schema: "core",
                table: "audit_logs");

            migrationBuilder.CreateIndex(
                name: "ix_audit_logs_actor_id",
                schema: "core",
                table: "audit_logs",
                column: "actor_id");

            migrationBuilder.CreateIndex(
                name: "ix_audit_logs_subject_user_id",
                schema: "core",
                table: "audit_logs",
                column: "subject_user_id");
        }
    }
}
