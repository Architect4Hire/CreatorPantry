---
name: creatorpantry-design-system
description: Build or modify the installed CreatorPantry Angular 22 tokens, components, patterns, and showcase.
---

# CreatorPantry Design-System Workflow

Read `src/web/README.md`, `src/web/DESIGN-SYSTEM.md`, `src/web/CLAUDE.md`, the library `public-api.ts`, and current semantic tokens before changing UI.

## Current foundation

The reusable package is `@creator-pantry/ui` at `src/web/projects/creator-pantry-ui`. Its public API already contains theme service, button, card, badge, field, progress, dialog, and quick-action components. Reuse or compose these before adding anything.

## Confirmations use SweetAlert2, not `CpDialogComponent`

A **confirmation** — a question with two answers, one of which loses work the creator cannot get back — goes through `ConfirmService` (`src/web/src/app/core/confirm.service.ts`), which wraps [SweetAlert2](https://sweetalert2.github.io/). Do not hand-build a confirm dialog and do not use `CpDialogComponent` for one.

- **`ConfirmService` is the only place `sweetalert2` may be imported.** Components inject the service, so the dependency stays swappable and every confirmation is stubbable in a test without rendering a modal.
- **`CpDialogComponent` remains correct for a modal that _hosts_ something** — a form, a picker, projected content with its own validation and focus order. The workspace-creation dialog is the reference example.
- **The package belongs to the application, never to the library.** `sweetalert2` is a dependency of `src/web` and is used only under `src/app`. Adding a runtime dependency to `projects/creator-pantry-ui` would force it on every consumer of `@creator-pantry/ui`.
- **Styling is in `src/web/src/styles.css`, against `--cp-*` tokens**, applied through the `cp-swal*` `customClass` names with `buttonsStyling: false`. SweetAlert2 renders into `<body>`, outside Angular's style encapsulation, so this cannot live in a component stylesheet — and without it a confirmation arrives in SweetAlert2's own palette and ignores dark mode. This is the one sanctioned exception to "style components with tokens in their own stylesheet"; it is not licence for a second token vocabulary.
- **Dismissing means "stay".** Escape, the backdrop and a programmatic close all resolve `false`, and the cancelling button holds focus so a reflexive Enter cannot discard work.
- **A browser close, refresh or full navigation cannot be confirmed by any library.** The browser shows its own message. That path stays a `beforeunload` listener; the two are not interchangeable.

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
