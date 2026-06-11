-- =============================================================================
-- Schema v3 — label reference edges.
--
-- Tier C of the search-coverage rollout. Answers "what AOT objects use
-- @SYS:123?" — the reverse-lookup direction of the labels FTS (which goes
-- label-key -> label-value).
--
-- Distinct from labels: `labels` carries the canonical label entry per
-- (file, key, language). `label_refs` carries pointers TO a (file, key)
-- from an AOT object's property — table.label, edt.helpText,
-- form.design.caption, etc.
--
-- label_file is the bare module / file token (e.g. "SYS"), not the full
-- AxLabelFile object name (which carries a language suffix). For
-- references like '@SomeLocalLabel' with no explicit file, label_file is
-- the empty string and label_key carries the raw token. The query side
-- accepts either form.
-- =============================================================================

CREATE TABLE label_refs (
    id                  INTEGER PRIMARY KEY,
    source_object_id    INTEGER NOT NULL REFERENCES objects(id) ON DELETE CASCADE,
    source_member       TEXT,
    label_file          TEXT    NOT NULL COLLATE NOCASE DEFAULT '',
    label_key           TEXT    NOT NULL COLLATE NOCASE,
    reference_kind      TEXT    NOT NULL,
    context             TEXT
);

CREATE INDEX idx_label_refs_target
    ON label_refs (label_file, label_key);

CREATE INDEX idx_label_refs_key
    ON label_refs (label_key);

CREATE INDEX idx_label_refs_source
    ON label_refs (source_object_id);

CREATE INDEX idx_label_refs_kind
    ON label_refs (reference_kind);

UPDATE schema_version SET version = 3, applied_at = strftime('%s','now') WHERE id = 1;
