---
name: creatorpantry-design-system
description: Build or modify the installed CreatorPantry Angular 22 tokens, components, patterns, and showcase.
---

# CreatorPantry Design-System Workflow

Read `src/web/README.md`, `src/web/DESIGN-SYSTEM.md`, `src/web/CLAUDE.md`, the library `public-api.ts`, and current semantic tokens before changing UI.

## Current foundation

The reusable package is `@creator-pantry/ui` at `src/web/projects/creator-pantry-ui`. Its public API already contains theme service, button, card, badge, field, progress, dialog, and quick-action components. Reuse or compose these before adding anything.

## Workflow

1. Classify the request as token, primitive, reusable component, product pattern, or feature composition.
2. Search `public-api.ts`, component source, and the showcase for existing behavior.
3. Decide whether the work is domain-neutral enough for the library. Recipe/content/media workflow code normally stays in `src/web/src/app/features/`.
4. Define the contract first: selector, typed signal inputs/outputs, projected regions, ARIA semantics, states, responsive behavior, and theme behavior.
5. Prefer composition over adding variants to a low-level component.
6. Implement standalone Angular 22 with strict templates and `OnPush`.
7. Style with semantic `--cp-*` tokens only. A new semantic intent is added to both light and dark themes.
8. Export intentional public components from `projects/creator-pantry-ui/src/public-api.ts`.
9. Add or update a realistic CreatorPantry showcase example in `src/web/src/app/`.
10. Verify keyboard operation, visible focus, screen-reader naming, 200% zoom, long text, narrow layout, light/dark themes, reduced motion, and empty/error/disabled states.
11. From `src/web/`, run `npm run build` and `npm test`.
12. Update `src/web/README.md` and `src/web/DESIGN-SYSTEM.md` for public API or visual-contract changes.

Never deep-import library internals, place API calls or recipe business logic in the library, create a second token vocabulary, or commit `dist/`/`node_modules/`.
