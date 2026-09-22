---
name: add-workspace-entity
description: Add a workspace-owned entity with EF configuration, isolation, service boundaries, migration, and two-workspace tests.
---

# Add a Workspace Entity

1. Prove the entity is creator/workspace-owned rather than shared reference data.
2. Add required `WorkspaceId`, navigation only when useful, timestamps, concurrency token for edited content, and the common workspace-owned interface.
3. Configure required ownership, indexed foreign key, workspace-relative unique indexes, lengths, delete behavior, and global query filter.
4. Set workspace ownership from `IWorkspaceContext`; exclude it from create/update ViewModels and mapping.
5. Add repository methods that use stable ids and allow the query filter to enforce scope.
6. Add DataLayer, Business, Facade, ServiceModel, validator, and mapper changes through the standard seam.
7. Generate the EF migration; review it but do not hand-edit routine generated code.
8. Add tests with the same public id/natural key in two workspaces, cross-workspace read/update/delete attempts, cache/search scope, and unknown-workspace behavior.
9. Run `@workspace-isolation-auditor`.

Do not use workspace scoping for platform ingredient/unit/cuisine vocabulary. Do not globalize recipes, media, prompts, drafts, voice examples, or publication records.

