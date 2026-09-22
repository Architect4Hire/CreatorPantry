# API Contract

All product routes are versioned from the first endpoint: `/api/v1/...`.

## Resource conventions

- Workspace routes use `/api/v1/workspaces/{workspaceSlug}/...`.
- Use nouns for resources and explicit action resources only when CRUD semantics are insufficient.
- `POST` creates, `PUT` replaces when genuinely supported, `PATCH` applies an explicit partial-update document, and `DELETE` follows documented retention behavior.
- Long-running operations return `202 Accepted` with an operation/job resource.
- Publishing and other retryable commands accept an idempotency key.

## Shapes

- Input uses ViewModels; output uses ServiceModels.
- Use ISO 8601 UTC timestamps and explicit local-zone fields when editorial schedules depend on a workspace timezone.
- Paginated collections use a stable cursor for large/changing datasets.
- Errors use `ProblemDetails` plus a stable machine-readable error code and correlation id.
- Concurrency-sensitive updates use row versions/ETags or an equivalent explicit check.

## Compatibility

Adding optional fields is usually compatible. Renaming/removing fields, changing meaning, changing nullability, narrowing accepted values, or changing ordering/pagination semantics is breaking. Add a new API version or migration path rather than quietly changing a shipped contract.

## Examples

```text
POST /api/v1/workspaces/{workspaceSlug}/recipes
GET  /api/v1/workspaces/{workspaceSlug}/recipes/{recipeId}
POST /api/v1/workspaces/{workspaceSlug}/recipes/{recipeId}/versions
POST /api/v1/workspaces/{workspaceSlug}/content-projects/{id}/generations
POST /api/v1/workspaces/{workspaceSlug}/publications
GET  /api/v1/workspaces/{workspaceSlug}/operations/{operationId}
```

