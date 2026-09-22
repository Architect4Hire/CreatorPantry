---
name: add-endpoint
description: Build or extend a CreatorPantry HTTP endpoint through the complete Controller to Repository/Gateway seam with tests.
---

# Add an Endpoint

Use this skill for any API route. Do not implement a controller-only shortcut.

## 1. Classify the endpoint

Answer before editing:

- Is the resource platform reference or workspace-owned?
- Is the operation read, create, update, delete, command, export, or long-running job?
- What membership role and resource-level rules apply?
- What is canonical source data versus a derivative?
- Does it require concurrency, idempotency, a transaction, an outbox/job, caching, or a provider call?
- What stable error codes and compatibility commitments does the route introduce?

Workspace-owned routes use `/api/v1/workspaces/{workspaceSlug}/...`. Never bind `WorkspaceId` from the payload.

## 2. Define the contract first

Create or update:

1. Request ViewModel under the API contract area.
2. FluentValidation validator for shape/format limits.
3. Response ServiceModel under Domain managers/models.
4. Explicit result/error types when the operation has expected failures.
5. OpenAPI/contract test and matching Angular model if the web feature consumes it.

Do not return EF entities, domain persistence models, provider DTOs, or anonymous objects.

Example route:

```text
POST /api/v1/workspaces/{workspaceSlug}/recipes
```

Example seam:

```text
CreateRecipeViewModel
  → RecipesController
  → IRecipeFacade / RecipeFacade
  → IRecipeBusiness / RecipeBusiness
  → IRecipeDataLayer / RecipeDataLayer
  → IRecipeRepository / RecipeRepository
  → CreatorPantryDbContext
```

## 3. Build bottom-up

### Repository

- Write the narrow EF query/persistence methods the operation needs.
- Rely on the workspace query filter and also use ownership-aware keys/constraints.
- Use no-tracking reads where safe.
- Keep provider calls, cache logic, and business decisions out.
- Add repository/integration tests for query shape and workspace filtering.

### Gateway, if required

- Add an application-owned interface and provider adapter.
- Configure typed `HttpClient`, authentication, timeout, resilience, and telemetry.
- Map provider data at the boundary.
- Persist external ids in mapping/integration records.
- Design idempotency and ambiguous-response reconciliation before writes.

### DataLayer

- Compose repository/gateway calls into a complete data operation.
- Own the transaction boundary.
- Implement cache-first/staleness mechanics when needed, without deciding domain policy.
- Enqueue an outbox/durable job only after or atomically with committed state.
- Return domain/application data to Business.

### Business

- Translate ViewModel/application inputs into domain operations.
- Enforce invariants and resource-level authorization.
- Perform deterministic calculations.
- Preserve creator source material and source/derivative relationships.
- Map domain results to ServiceModels.
- Call only DataLayer.

### Facade

- Validate input and resolved workspace context.
- Coordinate membership policy and cache reads/writes.
- Call Business once per coherent application operation.
- Expose the boundary reused by controllers, workers, and AI plugins.
- Do not use EF, repositories, or provider clients.

### Controller

- Versioned route and correct authorization policy.
- Bind route and ViewModel; do not bind ownership ids.
- Call the facade and map explicit outcomes to HTTP.
- Use `201 Created` with a location for creates, `202 Accepted` for durable long work, and `ProblemDetails` for failures.
- Forward `CancellationToken`.

## 4. Cross-cutting decisions

### Workspace isolation

Resolve workspace from the route, verify membership, and use `IWorkspaceContext`. Cache and search keys include workspace scope. Add Workspace A/Workspace B tests.

### Concurrency

For editable creator content, use a row version/ETag or explicit expected version. A stale write returns a stable conflict response and preserves both the server state and the creator's attempted changes.

### Idempotency

Create/publish/generate commands likely to be retried accept an idempotency key. Scope it to workspace + operation + caller as appropriate and return the original committed result on replay.

### Long work

AI generation, imports, exports, media processing, and publishing usually create an operation/job and return `202`. The API request must not remain open around unbounded provider work.

### Audit

Record sensitive mutations: publishing confirmation, provider connection changes, role changes, content deletion, automation approval, and accepted AI replacement.

## 5. Tests

At minimum:

- Validator accepts valid input and rejects boundary cases.
- Business tests cover invariants and authorization.
- Data/repository tests cover persistence and transactions.
- Controller/contract test covers route, status, shape, and stable error code.
- Two-workspace test proves isolation.
- Concurrency and idempotency tests where applicable.
- Provider success, rate limit, timeout, ambiguous failure, and retry tests where applicable.
- Frontend service/component tests if a UI consumer is added.

## 6. Verification

Run focused tests, `dotnet test`, relevant Angular tests/build, and the appropriate read-only reviewer agents. Report the route, seam files, data classification, policy, verification, and remaining decisions.

## Completion checklist

- [ ] Route is versioned and workspace-shaped correctly.
- [ ] ViewModel/validator and ServiceModel are explicit.
- [ ] Every layer calls only the next layer.
- [ ] Workspace id is server-derived.
- [ ] EF/provider types stay internal.
- [ ] Cancellation flows through I/O.
- [ ] Transactions, concurrency, idempotency, and jobs are deliberate.
- [ ] Source content is not silently overwritten.
- [ ] Tests include a cross-workspace case.
- [ ] Contract and Angular models agree.

