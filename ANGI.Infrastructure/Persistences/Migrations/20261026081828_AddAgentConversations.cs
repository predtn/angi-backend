using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ANGI.Infrastructure.Persistences.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentConversations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "version",
                schema: "core",
                table: "roadmaps",
                type: "bigint",
                nullable: false,
                defaultValueSql: "1");

            migrationBuilder.CreateTable(
                name: "agent_conversations",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<int>(type: "integer", nullable: false),
                    roadmap_id = table.Column<long>(type: "bigint", nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    state = table.Column<string>(type: "jsonb", nullable: false),
                    current_proposal_id = table.Column<long>(type: "bigint", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    processing_turn_id = table.Column<long>(type: "bigint", nullable: true),
                    processing_attempt_id = table.Column<long>(type: "bigint", nullable: true),
                    processing_until = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agent_conversations", x => x.id);
                    table.CheckConstraint("ck_agent_conversations_lease", "(processing_turn_id IS NULL AND processing_attempt_id IS NULL AND processing_until IS NULL) OR (processing_turn_id IS NOT NULL AND processing_attempt_id IS NOT NULL AND processing_until IS NOT NULL)");
                    table.CheckConstraint("ck_agent_conversations_processing_ids", "(processing_turn_id IS NULL OR processing_turn_id > 0) AND (processing_attempt_id IS NULL OR processing_attempt_id > 0)");
                    table.CheckConstraint("ck_agent_conversations_status", "status IN ('active', 'closed', 'expired')");
                    table.CheckConstraint("ck_agent_conversations_version", "version > 0");
                    table.ForeignKey(
                        name: "fk_agent_conversations_roadmaps_roadmap_id",
                        column: x => x.roadmap_id,
                        principalSchema: "core",
                        principalTable: "roadmaps",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_agent_conversations_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "core",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "agent_messages",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    conversation_id = table.Column<long>(type: "bigint", nullable: false),
                    sequence_no = table.Column<int>(type: "integer", nullable: false),
                    turn_id = table.Column<long>(type: "bigint", nullable: false),
                    client_message_id = table.Column<long>(type: "bigint", nullable: true),
                    role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    processing_status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    request_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    response_payload = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agent_messages", x => x.id);
                    table.CheckConstraint("ck_agent_messages_ids", "turn_id > 0 AND (client_message_id IS NULL OR client_message_id > 0)");
                    table.CheckConstraint("ck_agent_messages_processing_status", "processing_status IN ('processing', 'completed', 'failed')");
                    table.CheckConstraint("ck_agent_messages_retry", "(role = 'user' AND client_message_id IS NOT NULL AND processing_status IS NOT NULL AND request_hash IS NOT NULL) OR (role <> 'user' AND client_message_id IS NULL AND processing_status IS NULL AND request_hash IS NULL AND response_payload IS NULL)");
                    table.CheckConstraint("ck_agent_messages_role", "role IN ('user', 'assistant', 'tool')");
                    table.CheckConstraint("ck_agent_messages_sequence", "sequence_no > 0");
                    table.ForeignKey(
                        name: "fk_agent_messages_agent_conversations_conversation_id",
                        column: x => x.conversation_id,
                        principalSchema: "core",
                        principalTable: "agent_conversations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "agent_proposals",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    conversation_id = table.Column<long>(type: "bigint", nullable: false),
                    proposal_version = table.Column<int>(type: "integer", nullable: false),
                    operation = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    base_roadmap_id = table.Column<long>(type: "bigint", nullable: true),
                    base_roadmap_version = table.Column<long>(type: "bigint", nullable: true),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    validation_result = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    reco_request_ids = table.Column<Guid[]>(type: "uuid[]", nullable: false, defaultValueSql: "'{}'::uuid[]"),
                    result_roadmap_id = table.Column<long>(type: "bigint", nullable: true),
                    result_roadmap_version = table.Column<long>(type: "bigint", nullable: true),
                    commit_result = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    committed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agent_proposals", x => x.id);
                    table.UniqueConstraint("ak_agent_proposals_conversation_id_id", x => new { x.conversation_id, x.id });
                    table.CheckConstraint("ck_agent_proposals_base", "(operation = 'update' AND base_roadmap_id IS NOT NULL AND base_roadmap_version IS NOT NULL AND base_roadmap_version > 0) OR (operation = 'create' AND base_roadmap_id IS NULL AND base_roadmap_version IS NULL)");
                    table.CheckConstraint("ck_agent_proposals_commit", "(status = 'committed' AND result_roadmap_id IS NOT NULL AND result_roadmap_version IS NOT NULL AND result_roadmap_version > 0 AND commit_result IS NOT NULL AND committed_at IS NOT NULL) OR (status <> 'committed' AND result_roadmap_id IS NULL AND result_roadmap_version IS NULL AND commit_result IS NULL AND committed_at IS NULL)");
                    table.CheckConstraint("ck_agent_proposals_operation", "operation IN ('create', 'update')");
                    table.CheckConstraint("ck_agent_proposals_status", "status IN ('ready', 'invalid', 'superseded', 'committed', 'discarded', 'expired')");
                    table.CheckConstraint("ck_agent_proposals_version", "proposal_version > 0");
                    table.ForeignKey(
                        name: "fk_agent_proposals_agent_conversations_conversation_id",
                        column: x => x.conversation_id,
                        principalSchema: "core",
                        principalTable: "agent_conversations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_agent_proposals_roadmaps_base_roadmap_id",
                        column: x => x.base_roadmap_id,
                        principalSchema: "core",
                        principalTable: "roadmaps",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_agent_proposals_roadmaps_result_roadmap_id",
                        column: x => x.result_roadmap_id,
                        principalSchema: "core",
                        principalTable: "roadmaps",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_roadmaps_version",
                schema: "core",
                table: "roadmaps",
                sql: "version > 0");

            migrationBuilder.CreateIndex(
                name: "ix_agent_conversations_expires_at",
                schema: "core",
                table: "agent_conversations",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_agent_conversations_id_current_proposal_id",
                schema: "core",
                table: "agent_conversations",
                columns: new[] { "id", "current_proposal_id" });

            migrationBuilder.CreateIndex(
                name: "ix_agent_conversations_roadmap_id",
                schema: "core",
                table: "agent_conversations",
                column: "roadmap_id");

            migrationBuilder.CreateIndex(
                name: "ix_agent_conversations_user_id_updated_at",
                schema: "core",
                table: "agent_conversations",
                columns: new[] { "user_id", "updated_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_agent_messages_conversation_id_client_message_id",
                schema: "core",
                table: "agent_messages",
                columns: new[] { "conversation_id", "client_message_id" },
                unique: true,
                filter: "client_message_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_agent_messages_conversation_id_sequence_no",
                schema: "core",
                table: "agent_messages",
                columns: new[] { "conversation_id", "sequence_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_agent_proposals_base_roadmap_id",
                schema: "core",
                table: "agent_proposals",
                column: "base_roadmap_id");

            migrationBuilder.CreateIndex(
                name: "ix_agent_proposals_conversation_id_proposal_version",
                schema: "core",
                table: "agent_proposals",
                columns: new[] { "conversation_id", "proposal_version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_agent_proposals_result_roadmap_id",
                schema: "core",
                table: "agent_proposals",
                column: "result_roadmap_id");

            migrationBuilder.AddForeignKey(
                name: "fk_agent_conversations_agent_proposals_id_current_proposal_id",
                schema: "core",
                table: "agent_conversations",
                columns: new[] { "id", "current_proposal_id" },
                principalSchema: "core",
                principalTable: "agent_proposals",
                principalColumns: new[] { "conversation_id", "id" },
                onDelete: ReferentialAction.Restrict);

            // EF cannot represent aggregate revision triggers in its relational model.
            migrationBuilder.Sql(AgentRoadmapVersionSql.Up);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(AgentRoadmapVersionSql.Down);

            migrationBuilder.DropForeignKey(
                name: "fk_agent_conversations_agent_proposals_id_current_proposal_id",
                schema: "core",
                table: "agent_conversations");

            migrationBuilder.DropTable(
                name: "agent_messages",
                schema: "core");

            migrationBuilder.DropTable(
                name: "agent_proposals",
                schema: "core");

            migrationBuilder.DropTable(
                name: "agent_conversations",
                schema: "core");

            migrationBuilder.DropCheckConstraint(
                name: "ck_roadmaps_version",
                schema: "core",
                table: "roadmaps");

            migrationBuilder.DropColumn(
                name: "version",
                schema: "core",
                table: "roadmaps");
        }
    }
}
