---
name: add-content-feature
description: Implement content projects, drafts, templates, derivatives, editorial state, SEO metadata, or repurposing workflows.
---

# Add a Content Feature

1. Identify the canonical source records and pin the exact recipe/content version.
2. Define the derivative type, channel constraints, brand/audience profiles, and editable output.
3. Create a revision rather than overwriting accepted history.
4. Enforce explicit workflow transitions and permissions; status is not publication state.
5. Track source-version staleness and mark derivatives `NeedsReview` after canonical changes.
6. Keep channel/provider constraints in profiles/adapters rather than scattered prompt strings.
7. Do not invent SEO metrics or allow generated copy to change canonical recipe facts.
8. Add tests for transitions, stale-source detection, workspace isolation, revision history, and creator rejection/acceptance.
