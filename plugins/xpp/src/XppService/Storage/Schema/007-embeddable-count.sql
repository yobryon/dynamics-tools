-- =============================================================================
-- Schema v7 -- honest embedding progress denominator.
--
--   index_state.embeddable_count - the number of rows the embedder can actually
--                                  embed: methods with a non-empty source_code
--                                  plus labels with a non-empty value.
--
-- Why: xpp_status reported embedding_total as (method_count + label_count),
-- which counts rows the embedder deliberately skips -- its drain predicates
-- require length(trim(...)) > 0. Runtime-source objects carry empty
-- source_code by design, so ~24k methods (plus a few empty labels) can never be
-- embedded. The result was a denominator the embedder could never reach: a
-- fully-drained index sat at 98.2% forever and read as stalled.
--
-- Counting the empty rows costs ~1.4s, which is fine at index time but not on a
-- status RPC that is documented as cheap and gets polled at session start. So
-- it is maintained here alongside the other index_state summary counts (set by
-- the indexer's UpdateIndexState) and read O(1) by GetStatus.
--
-- Backfill: defaults to 0; GetStatus falls back to (method_count + label_count)
-- until the next sweep populates it, so the upgrade degrades to the old
-- behaviour rather than reporting a zero denominator.
-- =============================================================================

ALTER TABLE index_state
    ADD COLUMN embeddable_count INTEGER NOT NULL DEFAULT 0;

UPDATE schema_version SET version = 7, applied_at = strftime('%s','now') WHERE id = 1;
