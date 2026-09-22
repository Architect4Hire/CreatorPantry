# Aspire and Local Composition

`CreatorPantry.AppHost` is the single source of truth for the complete application model. Do not hide required infrastructure in manual setup scripts or developer-specific configuration.

## Required resources

- Gateway, Web host, API, Worker, and MigrationService projects.
- SQL Server 2025 with persistent local data.
- Redis.
- Blob/object storage or a compatible local emulator.
- Configured AI model and embedding endpoints; local substitutes may be used when supported.
- Angular development server through `AddJavaScriptApp` or the selected Aspire integration.

## Dependency order

- MigrationService waits for SQL and completes before API/Worker depend on the schema.
- API waits for SQL, Redis, storage, and required model resources.
- Gateway waits for API and Web.
- Worker waits for its queues/stores and the migrated database.
- Use `WaitFor`, health checks, and service references; do not use arbitrary sleeps.

## Configuration

- Use resource references and service discovery instead of literal localhost ports.
- Secrets are parameters marked secret and supplied through user-secrets or the deployment secret store.
- AppHost stays declarative; it contains no domain workflows.
- ServiceDefaults owns common OpenTelemetry, health, resilience, and service-discovery setup.
- Pin SQL Server to a version supporting the vector capabilities actually used.

## Verification

Run `aspire run`, inspect the dashboard, and verify health, logs, traces, dependencies, and clean startup from an empty data volume. A project that works only when services are started manually is incomplete.

