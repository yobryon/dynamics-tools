-- =============================================================================
-- Schema v2 — field-level reference edges.
--
-- Tier B of the search-coverage rollout. Where the v1 `refs` table answers
-- "object X references object Y", `field_refs` answers "object X references
-- field Y on table Z" — i.e. where a specific table field is actually used
-- across forms, queries, entities, and relation constraints.
--
-- Distinct table (rather than additional columns on `refs`) because:
--   - the queries differ: `refs` is keyed on target_object_name alone;
--     field_refs is keyed on (target_table, target_field).
--   - existing edges don't carry field-level info, so a unified table
--     would have NULL field columns for ~95% of rows.
--   - migration is non-destructive — old refs queries keep working.
--
-- source_member is the in-source identifier doing the referencing (control
-- name, range name, data-source field name, relation constraint name).
-- Used to disambiguate when the same object has many references to the
-- same field.
-- =============================================================================

CREATE TABLE field_refs (
    id                  INTEGER PRIMARY KEY,
    source_object_id    INTEGER NOT NULL REFERENCES objects(id) ON DELETE CASCADE,
    source_member       TEXT,
    target_table_name   TEXT    NOT NULL COLLATE NOCASE,
    target_field_name   TEXT    NOT NULL COLLATE NOCASE,
    reference_kind      TEXT    NOT NULL,
    context             TEXT
);

CREATE INDEX idx_field_refs_target_field
    ON field_refs (target_table_name, target_field_name);

CREATE INDEX idx_field_refs_target_table
    ON field_refs (target_table_name);

CREATE INDEX idx_field_refs_source
    ON field_refs (source_object_id);

CREATE INDEX idx_field_refs_kind
    ON field_refs (reference_kind);

UPDATE schema_version SET version = 2, applied_at = strftime('%s','now') WHERE id = 1;
