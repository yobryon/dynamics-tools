-- =============================================================================
-- Schema v4 — per-object phase-2 marker.
--
-- Fixes a regression in the incremental-sweep skip predicate. v1-v3 used
-- "no row in methods" as the skip gate, which works for code-bearing
-- objects (classes, tables with methods, etc.) but mis-treats objects
-- that legitimately have zero methods (AxLabelFile, AxSecurityPrivilege,
-- AxResource, AxTile, AxMenu, etc.) as "never processed" — so every
-- incremental sweep re-fetches them from the bridge unnecessarily.
--
-- The fix is a per-object marker: objects.last_phase2_at carries the
-- timestamp Phase 2 last visited the row. 0 means never. The incremental
-- skip predicate becomes "where last_phase2_at = 0" — independent of
-- whether the object ended up with any methods.
--
-- Backfill: existing rows with ANY child data (methods / refs /
-- field_refs / label_refs / labels) get marked as processed-at-migration-
-- time. That keeps the post-migration sweep small.
-- =============================================================================

ALTER TABLE objects
    ADD COLUMN last_phase2_at INTEGER NOT NULL DEFAULT 0;

UPDATE objects
SET last_phase2_at = strftime('%s','now')
WHERE EXISTS (SELECT 1 FROM methods     m  WHERE m.object_id          = objects.id)
   OR EXISTS (SELECT 1 FROM refs        r  WHERE r.source_object_id   = objects.id)
   OR EXISTS (SELECT 1 FROM field_refs  fr WHERE fr.source_object_id  = objects.id)
   OR EXISTS (SELECT 1 FROM label_refs  lr WHERE lr.source_object_id  = objects.id)
   OR EXISTS (SELECT 1 FROM labels      l  WHERE l.label_file_id      = objects.id);

CREATE INDEX idx_objects_last_phase2 ON objects (last_phase2_at);

UPDATE schema_version SET version = 4, applied_at = strftime('%s','now') WHERE id = 1;
