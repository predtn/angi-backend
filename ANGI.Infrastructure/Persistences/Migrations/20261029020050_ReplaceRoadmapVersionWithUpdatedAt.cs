using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ANGI.Infrastructure.Persistences.Migrations;

/// <summary>Replaces roadmap counters and persisted Agent metadata without deleting roadmaps or chat history.</summary>
public partial class ReplaceRoadmapVersionWithUpdatedAt : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(AgentRoadmapVersionSql.Down);
        migrationBuilder.Sql("""
            ALTER TABLE core.roadmaps DROP CONSTRAINT ck_roadmaps_version;
            ALTER TABLE core.agent_proposals DROP CONSTRAINT ck_agent_proposals_base;
            ALTER TABLE core.agent_proposals DROP CONSTRAINT ck_agent_proposals_commit;
            ALTER TABLE core.roadmaps ALTER COLUMN updated_at TYPE timestamp(6) with time zone;
            ALTER TABLE core.roadmaps ALTER COLUMN updated_at SET DEFAULT clock_timestamp();
            ALTER TABLE core.agent_proposals ADD COLUMN base_roadmap_updated_at timestamp(6) with time zone;
            ALTER TABLE core.agent_proposals ADD COLUMN result_roadmap_updated_at timestamp(6) with time zone;

            UPDATE core.agent_proposals p SET base_roadmap_updated_at = r.updated_at
                FROM core.roadmaps r WHERE p.base_roadmap_id = r.id;
            -- Historical timestamps were never recorded by the counter contract. Only the current revision
            -- can be recovered exactly; older committed replies use their recorded commit time for display.
            UPDATE core.agent_proposals p SET result_roadmap_updated_at =
                CASE WHEN p.result_roadmap_version = r.version THEN r.updated_at ELSE p.committed_at END
                FROM core.roadmaps r WHERE p.result_roadmap_id = r.id AND p.status = 'committed';

            -- Never reinterpret an old reviewed counter as a timestamp that could authorize a fresh write.
            UPDATE core.agent_proposals SET status = 'superseded' WHERE status IN ('ready', 'invalid');
            UPDATE core.agent_conversations c SET current_proposal_id = NULL
                FROM core.agent_proposals p WHERE c.current_proposal_id = p.id AND p.status = 'superseded';
            UPDATE core.agent_messages SET processing_status = 'failed', response_payload = NULL
                WHERE processing_status = 'processing';
            UPDATE core.agent_conversations SET processing_turn_id = NULL, processing_attempt_id = NULL,
                processing_until = NULL, version = version + 1;

            -- AgentJson rejects unmapped properties. Convert nested snapshots, staged state and durable
            -- replay responses as well as the relational columns; retain every message and commit ID.
            CREATE FUNCTION core.migrate_roadmap_timestamp_json(doc jsonb,
                base_stamp timestamptz DEFAULT NULL, result_stamp timestamptz DEFAULT NULL)
                RETURNS jsonb LANGUAGE plpgsql AS $$
            DECLARE output jsonb; field record; element jsonb; proposal_id bigint;
            BEGIN
                IF jsonb_typeof(doc) = 'array' THEN
                    output := '[]'::jsonb;
                    FOR element IN SELECT value FROM jsonb_array_elements(doc) LOOP
                        output := output || jsonb_build_array(core.migrate_roadmap_timestamp_json(element, base_stamp, result_stamp));
                    END LOOP;
                    RETURN output;
                ELSIF jsonb_typeof(doc) <> 'object' OR doc IS NULL THEN RETURN doc;
                END IF;
                proposal_id := COALESCE((doc->>'proposalId')::bigint, (doc->>'committedProposalId')::bigint);
                IF proposal_id IS NOT NULL THEN
                    SELECT base_roadmap_updated_at, result_roadmap_updated_at INTO base_stamp, result_stamp
                        FROM core.agent_proposals WHERE id = proposal_id;
                END IF;
                output := '{}'::jsonb;
                FOR field IN SELECT key, value FROM jsonb_each(doc) LOOP
                    CASE field.key
                        WHEN 'sourceRoadmapVersion' THEN CONTINUE;
                        WHEN 'baseRoadmapVersion' THEN
                            output := output || jsonb_build_object('baseRoadmapUpdatedAt',
                                to_char(base_stamp AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US"Z"'));
                        WHEN 'roadmapVersion' THEN
                            output := output || jsonb_build_object('roadmapUpdatedAt',
                                to_char(result_stamp AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US"Z"'));
                        ELSE output := output || jsonb_build_object(field.key,
                            core.migrate_roadmap_timestamp_json(field.value, base_stamp, result_stamp));
                    END CASE;
                END LOOP;
                RETURN output;
            END $$;
            UPDATE core.agent_proposals SET
                payload = core.migrate_roadmap_timestamp_json(payload, base_roadmap_updated_at, result_roadmap_updated_at),
                commit_result = core.migrate_roadmap_timestamp_json(commit_result, base_roadmap_updated_at, result_roadmap_updated_at);
            UPDATE core.agent_conversations SET state = core.migrate_roadmap_timestamp_json(state);
            UPDATE core.agent_messages SET payload = core.migrate_roadmap_timestamp_json(payload),
                response_payload = core.migrate_roadmap_timestamp_json(response_payload);
            DROP FUNCTION core.migrate_roadmap_timestamp_json(jsonb, timestamptz, timestamptz);

            ALTER TABLE core.roadmaps DROP COLUMN version;
            ALTER TABLE core.agent_proposals DROP COLUMN base_roadmap_version;
            ALTER TABLE core.agent_proposals DROP COLUMN result_roadmap_version;
            ALTER TABLE core.roadmaps ADD CONSTRAINT ck_roadmaps_updated_at CHECK (isfinite(updated_at));
            ALTER TABLE core.agent_proposals ADD CONSTRAINT ck_agent_proposals_base CHECK (
                (operation = 'update' AND base_roadmap_id IS NOT NULL AND base_roadmap_updated_at IS NOT NULL AND isfinite(base_roadmap_updated_at))
                OR (operation = 'create' AND base_roadmap_id IS NULL AND base_roadmap_updated_at IS NULL));
            ALTER TABLE core.agent_proposals ADD CONSTRAINT ck_agent_proposals_commit CHECK (
                (status = 'committed' AND result_roadmap_id IS NOT NULL AND result_roadmap_updated_at IS NOT NULL
                    AND isfinite(result_roadmap_updated_at) AND commit_result IS NOT NULL AND committed_at IS NOT NULL)
                OR (status <> 'committed' AND result_roadmap_id IS NULL AND result_roadmap_updated_at IS NULL
                    AND commit_result IS NULL AND committed_at IS NULL));
            """);
        migrationBuilder.Sql(RoadmapUpdatedAtSql.Up);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Removed roadmap counters cannot be reconstructed. Restore a pre-migration backup to downgrade.");
}
