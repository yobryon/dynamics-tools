# Bundled native dependencies

## sqlite-vec (`win-x64/vec0.dll`)

The [sqlite-vec](https://github.com/asg017/sqlite-vec) loadable extension that
provides the `vec0` virtual table for vector KNN (semantic search). No NuGet
package ships the native binary, and at 289 KB / single-arch (this product is
Windows-bound by the net48 metadata bridge) it's cheaper and more robust to
vendor it than to download it at runtime.

- Version: **v0.1.9** (`sqlite-vec-0.1.9-loadable-windows-x86_64`)
- License: MIT / Apache-2.0 (Alex Garcia). Redistributed under those terms.
- Loaded with the explicit entry point `sqlite3_vec_init` (the filename-derived
  default `sqlite3_vec0_init` is not exported).
- To update: download the `loadable-windows-x86_64` asset from the sqlite-vec
  releases, extract `vec0.dll`, replace this file, bump the version note.
