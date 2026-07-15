-- =============================================================================
-- Schema v6 -- label value hash for content-addressed re-embedding.
--
--   labels.value_hash - SHA-256 (hex) of the label's value text, written by the
--                       indexer's label upsert. Methods already carry
--                       source_hash; labels did not, so the label embedder's
--                       drain predicate could only re-embed on a missing meta
--                       row -- which relied on id churn from delete+reinsert.
--                       Now that labels are upserted in place (id preserved,
--                       embedding kept), the embedder needs a stored content
--                       hash to tell a changed value from an unchanged one.
--
-- Backfill: existing rows default to '' (unknown). The label drain predicate is
-- transition-safe -- it only trusts value_hash once it is non-empty -- so
-- existing rows keep their valid embeddings and populate value_hash lazily the
-- next time their label file is re-indexed. No mass re-embed on upgrade.
-- =============================================================================

ALTER TABLE labels
    ADD COLUMN value_hash TEXT NOT NULL DEFAULT '';

UPDATE schema_version SET version = 6, applied_at = strftime('%s','now') WHERE id = 1;
