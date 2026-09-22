# Backend Architecture

Every HTTP feature follows:

```text
Controller → Facade → Business → DataLayer → Repository | Gateway
```

## Layer ownership

### Controller

- Lives only in `CreatorPantry.ApiService/Controllers`.
- Accepts and validates HTTP-shaped ViewModels.
- Resolves route context and calls one facade operation.
- Maps expected results to consistent HTTP responses.
- Contains no EF Core, cache, external-provider, or domain logic.

### Facade

- Public application boundary used by controllers, AI plugins, and workers.
- Coordinates validation, membership/policy checks, cache lookup, and Business calls.
- Returns ServiceModels or explicit result types.
- Does not query `DbContext` or call repositories directly.

### Business

- Owns domain invariants, resource authorization, deterministic calculations, and model translation.
- Calls DataLayer only.
- Does not know about HTTP, EF tracking, provider SDK response types, or cache transport.

### DataLayer

- Composes complete persistence operations and owns transactions.
- Chooses repository and gateway calls and implements cache-first/staleness workflows.
- Does not make product decisions that belong in Business.

### Repository and Gateway

- Repositories contain EF Core queries and persistence only.
- Gateways contain external HTTP/SDK concerns only.
- Provider response types are mapped at the integration boundary.

## Contracts

- Interfaces exist between every layer and are injected through constructors.
- `CancellationToken` flows through every asynchronous call.
- ViewModels, ServiceModels, domain entities, EF entities, and provider DTOs have explicit boundaries.
- EF entities never cross the API boundary.
- Use `ProblemDetails` with stable error codes for failures.
- Avoid generic repositories when a purpose-built query communicates intent better.

## Validation and transactions

- Shape validation occurs at the edge with FluentValidation.
- Domain invariant validation occurs in Business.
- External constraints are verified by Gateways/DataLayer and translated into application results.
- DataLayer defines the transaction boundary for multi-write operations.
- Side effects that must follow a committed transaction use an outbox or durable job record.

## Defects

The following are architectural defects: `DbContext` in controllers/facades, a repository called from Business, provider SDK types in ServiceModels, `Facade/Business/Data` folders under the API project, synchronous blocking of asynchronous I/O, or tenant/workspace identifiers trusted from the client.

