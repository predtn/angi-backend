namespace ANGI.Infrastructure.Persistences.Migrations;

/// <summary>Database-only trigger definitions that EF cannot express in its relational model.</summary>
internal static class AgentRoadmapVersionSql
{
    // Transaction-local markers ensure one revision per changed roadmap, even for bulk child mutations.
    public const string Up = """
        CREATE FUNCTION core.track_roadmap_revision() RETURNS trigger LANGUAGE plpgsql AS $$
        DECLARE marker text := 'angi.roadmap_' || NEW.id::text;
        BEGIN
            IF TG_OP = 'INSERT' THEN
                PERFORM set_config(marker, txid_current()::text, true);
                NEW.version := 1;
            ELSE
                IF current_setting(marker, true) IS DISTINCT FROM txid_current()::text THEN
                    NEW.version := OLD.version + 1;
                    PERFORM set_config(marker, txid_current()::text, true);
                ELSE
                    NEW.version := OLD.version;
                END IF;
            END IF;
            RETURN NEW;
        END $$;
        CREATE TRIGGER trg_roadmap_revision BEFORE INSERT OR UPDATE ON core.roadmaps
            FOR EACH ROW EXECUTE FUNCTION core.track_roadmap_revision();

        CREATE FUNCTION core.track_roadmap_child_revision() RETURNS trigger LANGUAGE plpgsql AS $$
        DECLARE old_parent bigint; new_parent bigint; parent bigint;
        BEGIN
            IF TG_TABLE_NAME = 'roadmap_days' THEN
                IF TG_OP <> 'INSERT' THEN old_parent := OLD.roadmap_id; END IF;
                IF TG_OP <> 'DELETE' THEN new_parent := NEW.roadmap_id; END IF;
            ELSE
                IF TG_OP <> 'INSERT' THEN
                    SELECT roadmap_id INTO old_parent FROM core.roadmap_days WHERE id = OLD.roadmap_day_id;
                END IF;
                IF TG_OP <> 'DELETE' THEN
                    SELECT roadmap_id INTO new_parent FROM core.roadmap_days WHERE id = NEW.roadmap_day_id;
                END IF;
            END IF;
            FOR parent IN SELECT DISTINCT value FROM unnest(ARRAY[old_parent, new_parent]) value WHERE value IS NOT NULL ORDER BY value LOOP
                IF current_setting('angi.roadmap_' || parent::text, true) IS DISTINCT FROM txid_current()::text THEN
                    UPDATE core.roadmaps SET version = version WHERE id = parent;
                END IF;
            END LOOP;
            RETURN NULL;
        END $$;
        CREATE TRIGGER trg_roadmap_day_revision AFTER INSERT OR UPDATE OR DELETE ON core.roadmap_days
            FOR EACH ROW EXECUTE FUNCTION core.track_roadmap_child_revision();
        CREATE TRIGGER trg_roadmap_item_revision AFTER INSERT OR UPDATE OR DELETE ON core.roadmap_items
            FOR EACH ROW EXECUTE FUNCTION core.track_roadmap_child_revision();
        """;

    public const string Down = """
        DROP TRIGGER IF EXISTS trg_roadmap_item_revision ON core.roadmap_items;
        DROP TRIGGER IF EXISTS trg_roadmap_day_revision ON core.roadmap_days;
        DROP TRIGGER IF EXISTS trg_roadmap_revision ON core.roadmaps;
        DROP FUNCTION IF EXISTS core.track_roadmap_child_revision();
        DROP FUNCTION IF EXISTS core.track_roadmap_revision();
        """;
}
