---
name: aspireify
description: Wire an existing CreatorPantry repository into an Aspire AppHost after scaffolding or when the resource graph changes.
---

# Aspireify CreatorPantry

1. Inspect the solution, projects, existing containers, configuration, health checks, and external dependencies.
2. Propose the resource graph before editing: SQL Server 2025, Redis, object storage/emulator, model resources, MigrationService, API, Worker, Angular/Web, and Gateway.
3. Keep `CreatorPantry.AppHost` declarative and `CreatorPantry.ServiceDefaults` responsible for shared telemetry, health, resilience, and discovery.
4. Add each project/resource once with stable logical names.
5. Wire `WithReference`, `WaitFor`, and `WaitForCompletion` according to real dependencies; migrations complete before schema consumers start.
6. Use secret parameters for credentials and service discovery for endpoints. Do not copy connection strings or localhost ports into application code.
7. Keep development substitutes compatible with production configuration shape.
8. Run the AppHost, inspect the dashboard, and verify clean startup, health, traces, and dependency wiring from an empty environment.

See [AppHost wiring](references/apphost-wiring.md).

