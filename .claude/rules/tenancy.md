# Workspace Tenancy

CreatorPantry is multi-workspace. A user may have a different role in every workspace. Identity authenticates the person; `WorkspaceMembership` authorizes actions within a workspace.

## Resolution

- Use a stable workspace slug or id in the route: `/api/v1/workspaces/{workspaceSlug}/...`.
- Resolve it server-side, confirm active membership, then create an immutable request-scoped `IWorkspaceContext`.
- Never accept `WorkspaceId` from a body, query string, client-controlled header, AI tool argument, or background-job payload without revalidation.
- Unknown workspaces and inaccessible workspaces both return 404.

## Data zones

Global reference entities have no `WorkspaceId`: `Ingredient`, `IngredientAlias`, `MeasurementUnit`, `MeasurementConversion`, `Cuisine`, `DietaryTag`, `Allergen`, `CookingTechnique`, `EquipmentType`, and `FoodCategory`.

Creator-owned entities are workspace-scoped: `Recipe`, `RecipeVersion`, `ContentProject`, `ContentDraft`, `MediaAsset`, `BrandProfile`, `VoiceProfile`, `SEOBrief`, `Publication`, `WorkspaceTemplate`, `AiGeneration`, and `AuditLog`.

Do not duplicate shared reference facts per workspace. Do not make creator intellectual property global for deduplication convenience.

## Persistence

- Every workspace entity implements the common workspace-owned contract and has a required indexed `WorkspaceId`.
- Apply a global EF Core query filter using `IWorkspaceContext`.
- Include `WorkspaceId` in alternate keys and unique indexes when uniqueness is workspace-relative.
- Set `WorkspaceId` server-side on creation. Reject attempts to change ownership by update mapping.
- `IgnoreQueryFilters()` is prohibited except in documented migration, erasure, platform-maintenance, or
  **background queue-claim** code paths. A queue claim is a carve-out because a worker looks for work before it
  knows which workspace it will serve, so no `IWorkspaceContext` exists to filter by. It carries two
  conditions: the query returns **identifiers only** — never a title, a snapshot, or any creator content — and
  the worker resolves and validates that workspace through the ordinary tenancy path before reading anything
  else. Finding a row is not authorization. Every such file is listed by path in
  `BulkOperationBoundaryTests.Exemptions`, so granting the exception costs a reviewable line.
- Background jobs resolve and validate their workspace before invoking a facade.

## Authorization

Initial membership roles are `Viewer`, `Contributor`, `Editor`, and `Owner`. `PlatformAdmin` is platform-wide and does not imply workspace ownership unless a separately audited support policy says so.

- Route-level access is handled by membership-aware policies.
- Resource-level rules stay in Business because they often require data.
- A facade may coordinate validation, policy evaluation, and caching but must not bypass Business authorization.

## Caching and AI

- Workspace cache keys start with `workspace:{workspaceId}:`.
- Global reference keys never include a workspace.
- Cache invalidation uses the same scope as the cached value.
- Prompts and embeddings for private content carry workspace ownership in storage and retrieval filters.
- Semantic Kernel plugins obtain workspace context from the caller; they never accept it from the model.

## Required tests

Every workspace feature includes a test with Workspace A and Workspace B proving that list, detail, mutation, search, cache, background processing, and AI retrieval cannot cross the boundary. A single-workspace test is not isolation coverage.

