using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ANGI.Infrastructure.Persistences.Migrations
{
    /// <inheritdoc />
    public partial class AddConversationPreferencesLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                DECLARE conflicts text;
                BEGIN
                    SELECT string_agg('roadmap ' || roadmap_id || ': conversations ' || ids, '; ')
                    INTO conflicts FROM (
                        SELECT roadmap_id, string_agg(id::text, ',' ORDER BY id) AS ids
                        FROM core.agent_conversations WHERE roadmap_id IS NOT NULL
                        GROUP BY roadmap_id HAVING count(*) > 1
                    ) duplicates;
                    IF conflicts IS NOT NULL THEN
                        RAISE EXCEPTION 'Resolve duplicate roadmap conversations before migration: %', conflicts;
                    END IF;
                END $$;
                """);
            migrationBuilder.DropCheckConstraint(
                name: "ck_outbox_messages_event_type",
                schema: "core",
                table: "outbox_messages");

            migrationBuilder.DropIndex(
                name: "ix_agent_conversations_roadmap_id",
                schema: "core",
                table: "agent_conversations");

            migrationBuilder.AddColumn<int>(
                name: "last_message_sequence",
                schema: "core",
                table: "agent_conversations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "last_proposal_version",
                schema: "core",
                table: "agent_conversations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "purged_client_message_id",
                schema: "core",
                table: "agent_conversations",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddCheckConstraint(
                name: "ck_outbox_messages_event_type",
                schema: "core",
                table: "outbox_messages",
                sql: "event_type IN ('DISH_UPSERTED', 'DISH_DEACTIVATED', 'FEEDBACK_CREATED', 'AGENT_IMPRESSION_RECORDED')");

            migrationBuilder.CreateIndex(
                name: "ix_agent_conversations_roadmap_id",
                schema: "core",
                table: "agent_conversations",
                column: "roadmap_id",
                unique: true,
                filter: "roadmap_id IS NOT NULL");

            migrationBuilder.Sql("""
                UPDATE core.agent_conversations c SET
                    last_message_sequence = COALESCE((SELECT max(sequence_no) FROM core.agent_messages WHERE conversation_id=c.id),0),
                    last_proposal_version = COALESCE((SELECT max(proposal_version) FROM core.agent_proposals WHERE conversation_id=c.id),0);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_outbox_messages_event_type",
                schema: "core",
                table: "outbox_messages");

            migrationBuilder.DropIndex(
                name: "ix_agent_conversations_roadmap_id",
                schema: "core",
                table: "agent_conversations");

            migrationBuilder.DropColumn(
                name: "last_message_sequence",
                schema: "core",
                table: "agent_conversations");

            migrationBuilder.DropColumn(
                name: "last_proposal_version",
                schema: "core",
                table: "agent_conversations");

            migrationBuilder.DropColumn(
                name: "purged_client_message_id",
                schema: "core",
                table: "agent_conversations");

            migrationBuilder.AddCheckConstraint(
                name: "ck_outbox_messages_event_type",
                schema: "core",
                table: "outbox_messages",
                sql: "event_type IN ('DISH_UPSERTED', 'DISH_DEACTIVATED', 'FEEDBACK_CREATED')");

            migrationBuilder.CreateIndex(
                name: "ix_agent_conversations_roadmap_id",
                schema: "core",
                table: "agent_conversations",
                column: "roadmap_id");
        }
    }
}
