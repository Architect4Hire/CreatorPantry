# CreatorPantry Claude Toolkit

This folder turns the root `CLAUDE.md` constitution into path-specific rules, procedural skills, specialist reviewers, and lightweight enforcement hooks.

## Precedence

1. Explicit user instruction for the current task.
2. Root `CLAUDE.md` project constitution.
3. The most specific rule under `.claude/rules/`.
4. The matching skill under `.claude/skills/`.
5. Existing repository conventions and nearby code.

When instructions conflict, stop and surface the conflict. Do not silently choose the weaker security, tenancy, content-ownership, or safety rule.

## Rules

| File | Purpose |
|---|---|
| `tenancy.md` | Workspace resolution, membership, query filters, cache keys, isolation tests |
| `backend.md` | Controller-to-repository seam, ownership of logic, models, validation, transactions |
| `aspire.md` | AppHost, dependencies, health, telemetry, resources, local development |
| `auth.md` | Identity, BFF sessions, workspace authorization, CSRF, secrets |
| `gateway.md` | Browser boundary, YARP routes, headers, cookies, rate limits |
| `api-contract.md` | URL shape, versioning, errors, pagination, idempotency, compatibility |
| `ai.md` | Model abstractions, plugins, prompts, grounding, evaluation, safety |
| `frontend.md` | Angular 22 structure, state, HTTP, accessibility, tests |
| `design-system.md` | Installed `@creator-pantry/ui` paths, tokens, public API, extension and visual verification rules |
| `recipes.md` | Canonical recipe model, versions, scaling, normalization, food-domain cautions |
| `content.md` | Source/derivative relationship, drafts, templates, editorial workflow |
| `media.md` | Uploads, derivatives, provenance, access, metadata |
| `publishing.md` | Provider-neutral delivery, confirmation, idempotency, reconciliation |
| `external.md` | Typed gateways, resilience, rate limits, webhooks, provider isolation |

## Skills

Use one primary skill for the seam being built. Supporting skills may be loaded when the primary skill explicitly intersects another boundary.

- `add-endpoint`: full HTTP-to-data vertical slice.
- `add-workspace-entity`: workspace-scoped persistence and isolation.
- `add-ai-capability`: model-backed generation, tools, structured outputs, and evaluation.
- `add-external-integration`: provider adapter or webhook boundary.
- `add-background-job`: queued, scheduled, retryable, or long-running work.
- `add-recipe-feature`: canonical recipe behavior and deterministic food math.
- `add-content-feature`: content project, derivatives, templates, and workflow.
- `add-media-feature`: uploads, metadata, derivatives, and AI-generated assets.
- `new-component`: Angular component implementation.
- `creatorpantry-design-system`: work inside the installed Angular 22 `@creator-pantry/ui` package and showcase.
- Aspire skills: orchestration, monitoring, deployment, and resource wiring.
- `playwright-cli`: browser-based behavior and visual verification.

## Review agents

Agents inspect and report. They do not own implementation unless the user explicitly assigns that role.

- `@workspace-isolation-auditor`
- `@api-contract-checker`
- `@architecture-reviewer`
- `@design-review`
- `@ai-safety-reviewer`
- `@integration-reviewer`
- `@test-gap-analyzer`
- `@skills-evals`

## Hooks

- `secret-guard.sh` blocks obvious credential material before it is written.
- `format.sh` applies repository formatters after edits when the required tool is available.

Hooks are a backstop, not the architecture. Keep them fast, deterministic, and easy to understand.

## How to use this toolkit

1. Read the root constitution and the nearest rule.
2. Select the matching skill.
3. Inspect the existing implementation and tests.
4. State assumptions and plan the seam.
5. Implement the smallest complete vertical slice.
6. Run focused tests and static checks.
7. Run the relevant reviewer agent as a read-only audit.
8. Report files changed, behavior added, verification run, and unresolved decisions.

Do not treat examples as product requirements. `TBD` and `DECIDE` markers identify decisions the team must make before implementation.
