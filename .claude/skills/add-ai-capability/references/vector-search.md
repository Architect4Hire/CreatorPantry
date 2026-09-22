# Vector Search Pattern

Use SQL Server 2025 vector support only when semantic similarity is materially better than structured or full-text search.

Candidate corpora include workspace recipe/content text, approved brand-voice examples, templates, and platform ingredient/technique reference descriptions. Keep global and workspace corpora distinct.

Each embedding record stores:

- owner scope (`Global` or `WorkspaceId`)
- source record id and source revision/version
- chunk type and ordinal
- normalized text hash
- embedding model/deployment and dimensions
- generated timestamp
- vector

Retrieval applies authorization/workspace filters before returning content to the model. Never search all workspaces and filter after ranking. Re-embed when the source hash or embedding model changes. Use a durable idempotent backfill job and checkpoint progress.

Do not embed secrets, raw provider credentials, transient logs, or unnecessary binary metadata. Preserve the authoritative source record; an embedding is a disposable index.

Tests cover dimension mismatch, stale embeddings, workspace isolation, global/reference inclusion, deleted source cleanup, empty results, and deterministic tie handling.

