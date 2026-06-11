-- =============================================================================
-- Schema v5 — runtime/binary-module awareness.
--
-- Two columns added so the indexer can surface binary-only modules (compiled
-- DLLs, no on-disk XML) alongside the disk-backed modules:
--
--   models.is_binary  - true when only the runtime provider sees the module.
--                       Disk wins on the dedupe; this flips on only when
--                       NO disk provider saw the module.
--
--   objects.source    - "disk" or "runtime" per object. Disk wins where both
--                       providers expose the same object. Runtime-tagged
--                       rows have no X++ source available (their methods
--                       table entries carry empty source_code), and any
--                       attempt to mutate them through the typed write
--                       tools will fail at the bridge.
--
-- Backfill: existing rows are tagged "disk" (the previous indexer only saw
-- the disk providers). Existing models keep is_binary=0 by default.
-- =============================================================================

ALTER TABLE models
    ADD COLUMN is_binary INTEGER NOT NULL DEFAULT 0;

ALTER TABLE objects
    ADD COLUMN source TEXT NOT NULL DEFAULT 'disk';

CREATE INDEX idx_objects_source ON objects (source);

UPDATE schema_version SET version = 5, applied_at = strftime('%s','now') WHERE id = 1;
