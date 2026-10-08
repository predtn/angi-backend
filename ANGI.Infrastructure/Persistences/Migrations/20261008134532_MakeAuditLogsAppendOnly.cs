using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ANGI.Infrastructure.Persistences.Migrations
{
    /// <inheritdoc />
    public partial class MakeAuditLogsAppendOnly : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // audit_logs is append-only (Data Dictionary, ADM-A1). A trigger rather than REVOKE, because
            // angi_backend owns the table and could grant itself UPDATE / DELETE back. Statement level,
            // since TRUNCATE triggers cannot be row level.
            migrationBuilder.Sql("""
                CREATE FUNCTION core.fn_audit_logs_append_only() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'core.audit_logs is append-only: % is not allowed', TG_OP;
                END;
                $$;

                CREATE TRIGGER trg_audit_logs_append_only
                BEFORE UPDATE OR DELETE OR TRUNCATE ON core.audit_logs
                FOR EACH STATEMENT EXECUTE FUNCTION core.fn_audit_logs_append_only();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_audit_logs_append_only ON core.audit_logs;
                DROP FUNCTION IF EXISTS core.fn_audit_logs_append_only();
                """);
        }
    }
}
