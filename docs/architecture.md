# CreatorPantry Architecture

`CLAUDE.md` and `.claude/rules/` are the architectural source of truth. This page indexes the
architecture records that refine them.

## Architecture decisions

- [Baseline decisions](architecture-decisions/baseline.md): workspace tenancy, BFF authentication,
  SQL Server 2025, Redis, blob storage, staged media, AI proposal lifecycle, unit systems, nutrition
  optionality, and Buffer as the first publishing adapter.

## Implemented architecture

- [Phases 0–2](implemented-architecture-phase-0-2.md): repository and runtime foundation
  (Aspire AppHost, ServiceDefaults, MigrationService, Angular workspace), identity/gateway/browser
  boundary (auth seam, gateway-signed internal tokens, BFF session), and workspace tenancy core
  (resolution middleware, query filter/interceptor, authorization policies, audit log, outbox).
