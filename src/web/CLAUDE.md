# Claude Code instructions — CreatorPantry UI

Read `README.md` and `DESIGN-SYSTEM.md` before changing UI code.

## Non-negotiable architecture

- Angular 22 standalone components; do not introduce NgModules.
- Signals for local state and derived state; RxJS for asynchronous streams and interop.
- `ChangeDetectionStrategy.OnPush` on every reusable component.
- Strict TypeScript and strict templates stay enabled.
- Reusable UI lives in `projects/creator-pantry-ui`; product workflows live in app feature folders.
- Use public exports from `@creator-pantry/ui`; never deep-import library internals.
- Use semantic `--cp-*` tokens. Do not hard-code theme colors in feature code.
- Keep components small, composable, keyboard accessible, and domain-neutral.

## Before editing

1. Identify whether the request is a token, primitive, component, pattern, or feature composition.
2. Search for an existing component before creating one.
3. State the accessibility semantics and all interactive states.
4. Make the smallest coherent change.

## Definition of done

- Build succeeds for the library and showcase.
- Public API exports are intentional.
- Inputs and outputs are typed.
- Light and dark themes both work.
- Keyboard and focus behavior are verified.
- Empty, loading, error, disabled, and long-content states are considered.
- Documentation and showcase usage are updated for any public API change.

Use `.claude/skills/design-system/SKILL.md` for the component workflow.
