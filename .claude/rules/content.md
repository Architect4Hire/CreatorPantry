# Content Projects and Derivatives

A `ContentProject` coordinates creator work around canonical sources. Derivative artifacts reference the source recipe/version and selected brand/audience profiles.

- Blog drafts, social captions, newsletter copy, recipe cards, SEO metadata, and visual briefs are derivatives—not independent recipe truth.
- Record the source version used for each derivative so stale content can be detected after recipe changes.
- Regeneration creates a new artifact revision; it does not erase accepted history.
- Templates contain structure and instructions, while workspace voice profiles contain brand-specific examples and constraints.
- Workflow state transitions are explicit and audited: `Idea → Draft → Review → Approved → Scheduled → Published → Archived` (tune before implementation).
- Status does not imply publication success; Publication records own provider delivery state.
- SEO fields are recommendations. Do not fabricate keyword metrics or ranking claims.
- Channel constraints (length, markup, image ratio) belong in channel profiles/adapters, not scattered prompt text.

When a canonical recipe changes, identify affected derivatives and mark them `NeedsReview`; do not rewrite them automatically unless an approved automation policy exists.

