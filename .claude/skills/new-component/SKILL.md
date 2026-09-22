---
name: new-component
description: Add an Angular 22 CreatorPantry component using the installed @creator-pantry/ui package and exact repository paths.
---

# Add an Angular Component

1. Read `src/web/DESIGN-SYSTEM.md` and inspect `projects/creator-pantry-ui/src/public-api.ts` plus existing components.
2. Classify the work. Domain-neutral reusable UI belongs in `src/web/projects/creator-pantry-ui/src/lib/components/`; feature compositions belong in `src/web/src/app/features/`.
3. Reuse `CpButton`, `CpCard`, `CpBadge`, `CpField`, `CpProgress`, `CpDialog`, and `CpQuickAction` as applicable.
4. Define selector, typed signal inputs/outputs, projected content, ARIA behavior, states, responsive behavior, and theme behavior before implementation.
5. Use a standalone `cp-` component, strict templates, `OnPush`, semantic HTML, and accessible names.
6. Feature components use typed services/models and never inject `HttpClient` directly.
7. Use semantic `--cp-*` tokens only. If an intent is missing, update both themes rather than hard-coding a color.
8. Add the public export only when the component belongs to the reusable library.
9. Add a showcase example and tests for behavior/accessibility.
10. Verify light/dark themes, keyboard/focus, 200% zoom, long text, narrow layout, reduced motion, and empty/error/disabled states.
11. Run `npm run build` and `npm test` from `src/web/`, then run `@design-review`.

AI proposal components distinguish source from generated draft and expose Compare, Edit, Accept, and Discard. Media components expose processing/error, selection, rights, and alt-text state.

