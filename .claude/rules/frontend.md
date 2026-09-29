# Angular Frontend

The Angular 22 workspace lives at `src/web/`. Its reusable package is `@creator-pantry/ui` in `src/web/projects/creator-pantry-ui`; its current showcase/product shell is `src/web/src/`.

## Architecture

- Standalone components only; do not introduce NgModules.
- Strict TypeScript and strict templates remain enabled.
- Signals handle local/derived state; RxJS handles asynchronous streams and interop.
- Every reusable component uses `ChangeDetectionStrategy.OnPush`.
- Typed API services own HTTP; components never inject `HttpClient`.
- Feature code imports public exports from `@creator-pantry/ui` and never deep-imports library internals.
- Domain-neutral reusable UI goes into the library. Recipe, content, media, AI, and publishing workflows stay in feature folders.

## Product application structure

- `src/app/core/`: session, interceptors, guards, singleton infrastructure.
- `src/app/shared/`: application-level compositions built from `@creator-pantry/ui`; not a second primitive library.
- `src/app/features/`: lazy domain workflows.
- `src/app/models/`: centralized API models aligned with ServiceModels.
- `src/app/services/`: typed BFF/API services.

## Data and errors

- The browser calls relative BFF routes with credentials; no direct internal API URLs.
- Every data surface has loading, empty, degraded, error, and success states.
- Preserve unsaved creator edits through recoverable errors and navigation warnings.
- A form is a card, a legend, and named sections a `--cp-space-8` apart, built from `CpFormSectionComponent`.
  A feature stylesheet that redraws a form heading, a section intro, a section-level server error or a field
  row is a defect. Optionality is stated once in the legend, never field by field.
- Fields stack one per line at `--cp-measure-field`, and the space left beside them is the layout working.
  `CpFieldRowComponent` is the exception, for values that are short *and* read as one set — three durations, a
  quantity and its unit. Prose, lists and anything with a `textarea` stay stacked however many there are.
  `RecipeEditorComponent` is the reference; `src/web/DESIGN-SYSTEM.md` has the rules.
- A form that one Save writes keeps dirty state and validation per form, never per panel: an edit in a tab that
  is not showing still counts and still submits, and a field error selects the tab holding it before scrolling
  to and focusing the field. A panel that mounts lazily must not be able to swallow either.
- Generated content is identified and editable before acceptance.
- Use `async`, signals, or `takeUntilDestroyed`; no unmanaged subscriptions.
- Never use `any`; decode unknown data at boundaries.

## Styling and accessibility

- Import `tokens.css`, `themes.css`, and `global.css` once and in that order.
- Use semantic `--cp-*` tokens; literal theme colors in feature code are defects.
- Preserve visible focus, semantic HTML, accessible names, adjacent field errors, and status conveyed by more than color.
- Target WCAG 2.2 AA, 40px minimum touch targets, 200% zoom, narrow layouts, and reduced motion.

## Verification

From `src/web/`, run `npm ci`, `npm run build`, and `npm test`. Public UI changes also update the showcase, `README.md`, `DESIGN-SYSTEM.md`, and `projects/creator-pantry-ui/src/public-api.ts` as appropriate.

