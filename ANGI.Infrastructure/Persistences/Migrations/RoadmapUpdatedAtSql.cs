namespace ANGI.Infrastructure.Persistences.Migrations;

/// <summary>Uses the parent's database-generated microsecond timestamp as the aggregate concurrency token.</summary>
internal static class RoadmapUpdatedAtSql
{
    public const string Up = """
        CREATE FUNCTION core.track_roadmap_updated_at() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP = 'INSERT' THEN
                NEW.updated_at := clock_timestamp();
            ELSE
                -- Clock ties and backwards clock adjustments must never reuse an earlier token.
                NEW.updated_at := GREATEST(clock_timestamp(), OLD.updated_at + interval '1 microsecond');
            END IF;
            RETURN NEW;
        END $$;
        CREATE TRIGGER trg_roadmap_updated_at BEFORE INSERT OR UPDATE ON core.roadmaps
            FOR EACH ROW EXECUTE FUNCTION core.track_roadmap_updated_at();

        CREATE FUNCTION core.track_roadmap_child_updated_at() RETURNS trigger LANGUAGE plpgsql AS $$
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
            FOR parent IN SELECT DISTINCT value FROM unnest(ARRAY[old_parent, new_parent]) value
                WHERE value IS NOT NULL ORDER BY value LOOP
                UPDATE core.roadmaps SET updated_at = updated_at WHERE id = parent;
            END LOOP;
            RETURN NULL;
        END $$;
        CREATE TRIGGER trg_roadmap_day_updated_at AFTER INSERT OR UPDATE OR DELETE ON core.roadmap_days
            FOR EACH ROW EXECUTE FUNCTION core.track_roadmap_child_updated_at();
        CREATE TRIGGER trg_roadmap_item_updated_at AFTER INSERT OR UPDATE OR DELETE ON core.roadmap_items
            FOR EACH ROW EXECUTE FUNCTION core.track_roadmap_child_updated_at();
        """;

    public const string Down = """
        DROP TRIGGER IF EXISTS trg_roadmap_item_updated_at ON core.roadmap_items;
        DROP TRIGGER IF EXISTS trg_roadmap_day_updated_at ON core.roadmap_days;
        DROP TRIGGER IF EXISTS trg_roadmap_updated_at ON core.roadmaps;
        DROP FUNCTION IF EXISTS core.track_roadmap_child_updated_at();
        DROP FUNCTION IF EXISTS core.track_roadmap_updated_at();
        """;
}
