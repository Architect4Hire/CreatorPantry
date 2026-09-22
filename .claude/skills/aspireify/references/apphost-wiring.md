# CreatorPantry AppHost Wiring Reference

Target graph:

```text
SQL Server 2025 ─┬─> MigrationService ─completed─> API
                 └───────────────────────────────> Worker
Redis ───────────────────────────────────────────> API, Gateway, Worker
Object storage ──────────────────────────────────> API, Worker
Chat/embedding resources ────────────────────────> API, Worker
API + Web ───────────────────────────────────────> Gateway
```

Use stable logical names such as `sql`, `creatorpantrydb`, `cache`, `storage`, `chat`, `embeddings`, `api`, `worker`, `web`, and `gateway`. Confirm exact package and API syntax against the installed Aspire version before editing.

The MigrationService is run-once and must finish before API and Worker use the schema. Health checks indicate readiness; arbitrary delays do not. Aspire orchestrates the Angular development server, while the production Web project serves the built bundle.

