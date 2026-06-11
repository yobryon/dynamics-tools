-- =============================================================================
-- XppService v1 schema — initial.
--
-- See docs/v2-schema-design.md (in the parent design conversation) for the
-- rationale behind each table. In short: this is a query accelerator, not a
-- complete D365 metadata replica. Detailed inspection always hits the bridge.
--
-- Rules of the road:
--   - NOCASE collation on name-bearing columns (X++ names are case-insensitive).
--   - Single-writer task on the service side; WAL mode lets readers run in
--     parallel without blocking.
--   - target_object_name in `refs` is a string (not FK) on purpose: we don't
--     always know the target's model at reference-extraction time, and a
--     forward-reference during bulk import shouldn't deadlock against FK
--     enforcement.
-- =============================================================================

CREATE TABLE schema_version (
    id          INTEGER PRIMARY KEY CHECK (id = 1),
    version     INTEGER NOT NULL,
    applied_at  INTEGER NOT NULL
);

CREATE TABLE models (
    name              TEXT PRIMARY KEY COLLATE NOCASE,
    display_name      TEXT,
    publisher         TEXT,
    version           TEXT,
    layer             TEXT,
    is_custom         INTEGER NOT NULL DEFAULT 0,
    dependencies_json TEXT,
    last_indexed      INTEGER
);

CREATE TABLE objects (
    id            INTEGER PRIMARY KEY,
    name          TEXT NOT NULL COLLATE NOCASE,
    ax_type       TEXT NOT NULL,
    model         TEXT NOT NULL COLLATE NOCASE
                       REFERENCES models(name) ON DELETE CASCADE,
    file_path     TEXT NOT NULL,
    last_modified INTEGER NOT NULL,
    last_indexed  INTEGER NOT NULL,
    content_hash  TEXT NOT NULL,
    UNIQUE (name, ax_type, model)
);
CREATE INDEX idx_objects_name             ON objects (name);
CREATE INDEX idx_objects_ax_type_name     ON objects (ax_type, name);
CREATE INDEX idx_objects_model            ON objects (model);
CREATE INDEX idx_objects_last_modified    ON objects (last_modified);

CREATE TABLE methods (
    id              INTEGER PRIMARY KEY,
    object_id       INTEGER NOT NULL REFERENCES objects(id) ON DELETE CASCADE,
    name            TEXT NOT NULL COLLATE NOCASE,
    signature       TEXT,
    is_static       INTEGER NOT NULL DEFAULT 0,
    access_level    TEXT,
    return_type     TEXT,
    source_code     TEXT NOT NULL,
    source_hash     TEXT NOT NULL,
    line_count      INTEGER NOT NULL,
    parameters_json TEXT,
    UNIQUE (object_id, name)
);
CREATE INDEX idx_methods_object ON methods (object_id);
CREATE INDEX idx_methods_name   ON methods (name);

-- FTS5 over method source. Contentless index (content='methods') reads from
-- the methods table via content_rowid='id'. Triggers below keep it in sync.
CREATE VIRTUAL TABLE methods_fts USING fts5 (
    source_code,
    content      = 'methods',
    content_rowid = 'id',
    tokenize     = 'unicode61 remove_diacritics 0 categories ''L* N* Co'''
);

CREATE TRIGGER methods_ai AFTER INSERT ON methods BEGIN
    INSERT INTO methods_fts(rowid, source_code) VALUES (new.id, new.source_code);
END;
CREATE TRIGGER methods_ad AFTER DELETE ON methods BEGIN
    INSERT INTO methods_fts(methods_fts, rowid, source_code) VALUES ('delete', old.id, old.source_code);
END;
CREATE TRIGGER methods_au AFTER UPDATE ON methods BEGIN
    INSERT INTO methods_fts(methods_fts, rowid, source_code) VALUES ('delete', old.id, old.source_code);
    INSERT INTO methods_fts(rowid, source_code)              VALUES (new.id, new.source_code);
END;

-- Structural references from the metadata graph (form->table, class->base,
-- edt->extends, etc.). Source-code-level usages are recovered at query time
-- via FTS + token filtering, NOT stored here.
CREATE TABLE refs (
    id                  INTEGER PRIMARY KEY,
    source_object_id    INTEGER NOT NULL REFERENCES objects(id) ON DELETE CASCADE,
    target_object_name  TEXT NOT NULL COLLATE NOCASE,
    target_object_type  TEXT,
    reference_kind      TEXT NOT NULL,
    context             TEXT
);
CREATE INDEX idx_refs_target ON refs (target_object_name, target_object_type);
CREATE INDEX idx_refs_source ON refs (source_object_id);
CREATE INDEX idx_refs_kind   ON refs (reference_kind);

-- Label entries from AxLabelFile objects, one row per (label_file, key,
-- language). label_file_id targets objects.id (the AxLabelFile row created
-- in phase 1). 'key' is the bare label id WITHOUT the '@LabelFile:' prefix
-- used in X++ source; the prefix is reconstructed at query time using the
-- label file's name from objects.
CREATE TABLE labels (
    id              INTEGER PRIMARY KEY,
    label_file_id   INTEGER NOT NULL REFERENCES objects(id) ON DELETE CASCADE,
    key             TEXT    NOT NULL COLLATE NOCASE,
    value           TEXT    NOT NULL,
    language        TEXT    NOT NULL COLLATE NOCASE,
    description     TEXT,
    UNIQUE (label_file_id, key, language)
);
CREATE INDEX idx_labels_key  ON labels (key COLLATE NOCASE);
CREATE INDEX idx_labels_file ON labels (label_file_id);

-- FTS over label values: "find labels containing 'invoice posted'".
CREATE VIRTUAL TABLE labels_fts USING fts5 (
    value,
    content       = 'labels',
    content_rowid = 'id',
    tokenize      = 'unicode61 remove_diacritics 0 categories ''L* N* Co'''
);

CREATE TRIGGER labels_ai AFTER INSERT ON labels BEGIN
    INSERT INTO labels_fts(rowid, value) VALUES (new.id, new.value);
END;
CREATE TRIGGER labels_ad AFTER DELETE ON labels BEGIN
    INSERT INTO labels_fts(labels_fts, rowid, value) VALUES ('delete', old.id, old.value);
END;
CREATE TRIGGER labels_au AFTER UPDATE ON labels BEGIN
    INSERT INTO labels_fts(labels_fts, rowid, value) VALUES ('delete', old.id, old.value);
    INSERT INTO labels_fts(rowid, value)              VALUES (new.id, new.value);
END;

-- Per-label embedding metadata. Parallel shape to method_embedding_meta.
-- Labels are typically short enough that chunk_index=0 is the only chunk,
-- but we keep the column so the schema generalises if a label value is
-- ever long enough to split.
CREATE TABLE label_embedding_meta (
    id                INTEGER PRIMARY KEY,
    label_id          INTEGER NOT NULL REFERENCES labels(id) ON DELETE CASCADE,
    chunk_index       INTEGER NOT NULL,
    model_version     TEXT NOT NULL,
    chunk_text_hash   TEXT NOT NULL,
    last_computed     INTEGER NOT NULL,
    status            TEXT NOT NULL DEFAULT 'completed',
    error_message     TEXT,
    UNIQUE (label_id, chunk_index, model_version)
);
CREATE INDEX idx_label_embedding_meta_model  ON label_embedding_meta (model_version);
CREATE INDEX idx_label_embedding_meta_status ON label_embedding_meta (status);

-- Per-method embedding metadata. The actual vectors live in a vec0 virtual
-- table (created at runtime when sqlite-vec is loaded) keyed by the same id.
-- Schema is chunk-aware from day one so we can extend to multi-chunk long
-- methods without migration.
CREATE TABLE method_embedding_meta (
    id                INTEGER PRIMARY KEY,
    method_id         INTEGER NOT NULL REFERENCES methods(id) ON DELETE CASCADE,
    chunk_index       INTEGER NOT NULL,
    chunk_start_line  INTEGER NOT NULL,
    chunk_end_line    INTEGER NOT NULL,
    model_version     TEXT NOT NULL,
    chunk_text_hash   TEXT NOT NULL,
    last_computed     INTEGER NOT NULL,
    -- 'completed' = vector available and current
    -- 'in_progress' = an embedder worker is currently computing this; if
    --                 it's been in this state longer than the embedder's
    --                 stale timeout, it's safe to claim it
    -- 'error'      = the embedding attempt failed; error_message has
    --                details; embedder should skip on retry until source
    --                changes (chunk_text_hash bumps).
    status            TEXT NOT NULL DEFAULT 'completed',
    error_message     TEXT,
    UNIQUE (method_id, chunk_index, model_version)
);
CREATE INDEX idx_method_embedding_meta_model  ON method_embedding_meta (model_version);
-- Lets the embedder find pending work cheaply (rows where status != 'completed')
CREATE INDEX idx_method_embedding_meta_status ON method_embedding_meta (status);

-- Singleton summary updated by the writer. Counting 100k rows on every
-- GetStatus probe would be wasteful; we maintain the counts incrementally.
CREATE TABLE index_state (
    id                       INTEGER PRIMARY KEY CHECK (id = 1),
    last_full_scan_at        INTEGER,
    last_incremental_at      INTEGER,
    object_count             INTEGER NOT NULL DEFAULT 0,
    method_count             INTEGER NOT NULL DEFAULT 0,
    label_count              INTEGER NOT NULL DEFAULT 0,
    embedding_count          INTEGER NOT NULL DEFAULT 0,
    embedding_model_version  TEXT,
    notes                    TEXT
);
INSERT INTO index_state (id) VALUES (1);

INSERT INTO schema_version (id, version, applied_at)
VALUES (1, 1, strftime('%s','now'));
