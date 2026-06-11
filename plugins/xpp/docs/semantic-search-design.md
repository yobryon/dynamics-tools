# Semantic search — design & implementation plan

Status: **CODE-COMPLETE (2026-06-02, df3d33e).** All six components below are
built and green; every external dependency and vec0 SQL shape is verified
(see misc/EmbedProbe). The only thing left is a live in-service run — the
embedder populating the store and a real semantic query — which needs a
service restart to load the new build. This doc is the as-built record.

## Decision: runtime

**ONNX Runtime (`Microsoft.ML.OnnxRuntime`), not GGUF/llama.cpp.** Researched
2026-06-02. The usual "GGUF is faster" intuition is for *generation*; for
*embeddings* it inverts — a practitioner measured llama.cpp ~10x slower than
ONNX on a comparable embedding model (even ONNX-CPU beat llama.cpp-GPU), plus
llama.cpp has Qwen3 quirks and a per-token-vs-sentence pooling footgun. Qwen3
ships clean ONNX. A pure-C# engine (dotLLM, net10) exists but is preview.

**Seam: `Microsoft.Extensions.AI` `IEmbeddingGenerator<string, Embedding<float>>`.**
Our ONNX impl sits behind the standard M.E.AI contract so the backend is
swappable (Ollama / dotLLM / hosted) without touching callers.

Packages (resolved latest): Microsoft.ML.OnnxRuntime 1.26.0,
Microsoft.ML.Tokenizers 2.0.0, Microsoft.Extensions.AI.Abstractions 10.6.0.

## Decision: model + files

Repo: **`onnx-community/Qwen3-Embedding-0.6B-ONNX`** (Xenova, actively
maintained). Self-contained quantized variant **`onnx/model_quantized.onnx`**
(614 MB, int8 — best quality/size for retrieval; configurable to `model_q4f16`
567 MB smaller, or `model.onnx`+`model.onnx_data` fp32 best). Tokenizer/config
at repo ROOT: `tokenizer.json` (11.4 MB), `tokenizer_config.json`,
`config.json`, `special_tokens_map.json`, (`vocab.json` + `merges.txt` as the
BPE fallback).

Download URL pattern:
`https://huggingface.co/onnx-community/Qwen3-Embedding-0.6B-ONNX/resolve/main/<path>`

Native dim **1024**; Qwen3-Embedding supports **Matryoshka** truncation — store
a compact dim to bound the vec table. Storage math for ~1.3M items (947K
methods + 368K labels), float32: 1024-d ≈ 5.3 GB, 512-d ≈ 2.6 GB, 256-d ≈ 1.3
GB. **Default 512-d** (good quality, ~2.6 GB) — config knob `Embedding:Dim`.
(sqlite-vec also supports int8/bit vector quantization for further shrink.)

## Decision: vector store

**sqlite-vec `vec0`** virtual table, loaded as a runtime SQLite extension
(`connection.LoadExtension`). No NuGet bundles the native binary, so the
service **self-downloads `vec0.dll`** (win-x64, ~1 MB) from
`github.com/asg017/sqlite-vec/releases` into the runtime dir alongside the
model — consistent with the self-managed model acquisition. Schema comment in
`001-initial.sql` already anticipates "vectors live in a vec0 virtual table…
keyed by the same id".

## Decision: self-managed acquisition (like the index bootstrap)

A `ModelAcquisition` lifecycle component owns `%LOCALAPPDATA%\dynamics-xpp\
models\qwen3-embedding-0.6b\` (+ `runtime\vec0.dll`). On first need: if the
files are absent, download (HttpClient streaming, `.partial` → atomic rename,
a `.complete` marker carrying the model id + sha so a half-download re-pulls),
surfaced through status as an `embeddingModel` phase
(`absent`/`downloading`/`ready`/`error`) exactly like the index `warming`
phase. No `dev.ps1` step, no manual setup. (User may pre-place the files; the
component no-ops when `.complete` matches.)

## Components to build (order)

1. **Foundation (scaffolded):** `Embeddings/EmbeddingOptions.cs` (repo, variant,
   dim, dirs, batch), `Embeddings/EmbeddingPaths.cs` (resolve model/runtime
   dirs under the data dir).
2. **Acquisition:** `Embeddings/ModelAcquisition.cs` — streaming downloader +
   `.complete` marker + status. Downloads the model variant + tokenizer files +
   `vec0.dll`.
3. **Generator:** `Embeddings/QwenEmbeddingGenerator.cs : IEmbeddingGenerator
   <string, Embedding<float>>` — load `InferenceSession` (intra-op threads
   bounded so it doesn't fight the bridge pool); load the Qwen byte-level BPE
   via ML.Tokenizers (try `tokenizer.json`; fallback `BpeTokenizer.Create(vocab,
   merges)` with byte-level pre-tokenization). GenerateAsync: format (query gets
   the instruction prefix; documents raw) → tokenize → append EOS → run (input_
   ids + attention_mask) → **last-token pooling** (Qwen3 is a causal-LM embedder:
   take the last non-pad token's hidden state) → L2-normalize → Matryoshka
   truncate to Dim → re-normalize. Batch (left-pad to max len in batch).
   RISK: the exact ML.Tokenizers 2.0 API for loading a HF tokenizer.json — verify
   first; vocab.json+merges.txt is the fallback. Smoke: embed "CustTable" and a
   sentence, assert length==Dim and ||v||≈1.
4. **Schema 006-embeddings-vec.sql** (or runtime DDL, since vec0 must be loaded
   first): `CREATE VIRTUAL TABLE method_vec USING vec0(id INTEGER PRIMARY KEY,
   embedding float[512])` + `label_vec`. Created after LoadExtension succeeds.
   Wire LoadExtension into `IndexDatabase` connection open (guarded: if vec0.dll
   missing, semantic features are simply disabled, FTS keeps working).
5. **Embedder hosted service** `Embeddings/Embedder.cs` — after the structural
   index is ready, drains rows needing embeddings: select methods/labels whose
   `*_embedding_meta` is absent/stale (hash mismatch) for the current
   model_version; chunk long method bodies (schema already has chunk_index +
   start/end line); batch through the generator; write `vec0` + meta rows
   (status completed). Resumable (status + chunk_text_hash). Throttle (bounded
   concurrency, yield) so it doesn't starve search/bridge. Update
   `index_state.embedding_count` + `embedding_model_version`. Hook into
   `IndexLifecycle` (kick after sweep; also on write-through for changed objects).
6. **Search:** proto `SemanticSearch(query, limit, kind=method|label|object,
   mode=semantic|hybrid)`; `SearchHandlers` — embed query → `vec0` KNN
   (`MATCH` + `k`) → join methods/objects → return ranked. **Hybrid**: run FTS
   bm25 + vec cosine, fuse (Reciprocal Rank Fusion, k≈60). Expose
   `xpp_search_semantic` MCP tool (or a `mode` on xpp_search_code). Status gains
   `embeddingState` + `embeddingCount`/`embeddingTotal`.

## Risks / notes

- **Tokenizer API** (ML.Tokenizers 2.0 HF tokenizer.json) — the one real
  unknown; resolve before the generator. vocab.json+merges.txt fallback exists.
- **Last-token pooling + EOS** — Qwen3-Embedding appends `<|endoftext|>` and
  pools the last token; queries use the instruction format
  ("Instruct: …\nQuery: …"), documents are raw. Get this right or retrieval
  quality tanks.
- **Memory** — the ONNX session (int8 0.6B) is ~1-1.5 GB resident in the service
  process, *on top of* the bridge pool. The embedder pass is long (~1.3M items);
  run it background + throttled.
- **vec extension load path** — SQLite calls the platform loader directly; ensure
  the runtime dir is on PATH or pass the full path to LoadExtension.
- **net10-windows + ONNX Runtime native** — confirm the win-x64 native ships in
  the package's runtimes/ and copies to output.

Related: [[backlog-domain-model-loss-audit]] is unrelated; this supersedes the
"embedding skeleton unwired" note. The schema tables + status enum in
`001-initial.sql` are the pre-built half.
