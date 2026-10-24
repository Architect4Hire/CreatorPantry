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
- A routed page is one of three shapes — library, detail or flow — and draws no header, gutter, grid, facts
  list, step list, chip or sliding panel of its own, and no private copy of `.cp-muted` or `.cp-actions`.
  `scripts/check-page-contract.mjs` fails `npm test` when one does; an exemption is a listed path with a reason
  in that script, and a check is never loosened to let a page through.
- The shell's page frame owns the one gutter (`--cp-page-gutter`) and the one width every signed-in page has; a
  routed component that sets `padding` or `max-width` on its `:host` is a defect, and a page that needs another
  width asks for it with `data: { pageWidth: 'reading' | 'wide' }` on its route.
- The frame also owns a tool's tone. A creative tool's parent route declares `data: { tone: 'purple' | 'pink' |
  'blue' | 'orange' }` and the frame sets `data-cp-tone`; parts read `--cp-tone*`. A feature that sets
  `data-cp-tone` or declares a `--cp-tone*` is a defect, and the page contract's `tone` rule fails it.
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
- A page that lists many things of one kind is a library, built in DAM's order: `cp-page-head` with the one
  primary action, `cp-toolbar`, `cp-list-shell`, `ul[cpCardGrid]` of `cp-media-card`, and a footer with a
  result summary and "Load more". A feature stylesheet that redraws a search box, a chip, a card, a facts
  line or a Previous/Next pager for one is a defect. On a card: state as `cp-status-pill` then kind as
  `cp-badge` in the badges slot, measured and dated values as inline facts, the creator's words as the
  description. A few fixed answers filter with chips; many, or server-read ones, with a select.
- A library whose things are each in one of a few named, ordered states is a board: the same head, toolbar and
  list shell, with `[cpBoard]` of `cp-board-column` where the card grid would be. It is not a fourth shape. A
  move has two paths that send the same request — a drag, and a control on every card that names the column and
  the place from the keyboard — and a move that can only be dragged is a defect. The order, the counts and which
  moves are allowed are the server's: the page shows a move at once, and puts the card back and says so when the
  answer is no. `ContentBoardComponent` is the reference; `src/web/DESIGN-SYSTEM.md` has the rules.
- A page about one record, or a settings page, is a detail page: `cp-page-head` with one `cpBackLink` to the
  page it sits under — its text that page's route title, never "Back to…", present in every state, absent on
  a top-level page — then one fixed place for messages, then a column of `cp-topic-card` with each action at
  the bottom of the card it acts on. A hand-drawn `h1`, back link, facts list or card heading in a feature
  stylesheet is a defect, and so are tabs used to file one record's topics. A form one Save writes stays one
  card of `cp-form-section`s with its save state beside its Save; a topic card neither replaces nor wraps a
  form section.
- The dashboard is a detail page about the workspace whose topic cards sit in `ul[cpCardGrid]`, most urgent
  first. It shows the read model's counts and derives none, keeps an unreadable topic's card with a notice in
  it, and links each item to where it is dealt with. `DashboardComponent` is the reference.
- A page that takes the creator through steps in order is a flow: `cp-page-head` with no action, then the
  flow's `cp-progress` over a `cp-stepper` beside one wide `cp-topic-card` headed by the step's name, which
  holds the shell's legend, the step's `cp-form-section`s, the save status and Back / Continue. A step change
  moves focus to that card's heading. A step list, a step heading or a resume card drawn in a feature
  stylesheet is a defect. `BrandSetupShellComponent` and `ContentPipelineShellComponent` are the references.
- An edit to the record on screen that is not part of the page's own form opens in `CpDrawerComponent`. The
  handler that opens it focuses its button first, and a form inside it finds its fields from the document,
  not from its host element, because an open drawer moves its content to `<body>`.
- A task the creator does about the page while still reading it — a small edit to the record on screen, a
  preview, a tool — opens in `CpDrawerComponent`. One that needs an address, more than one step or the full
  width is a page; a short thing that does not need the page in view is `CpDialogComponent`; a yes/no is
  `ConfirmService`. A drawer never holds a form the page's Save writes, and a feature that closes one on
  `closeRequested` without confirming a discard of unsaved edits is a defect.
- Generated content is identified and editable before acceptance. The mark is `cp-generated-label` (or `cp-generated`
  around output with no edge of its own), never a badge or label of a feature's own; `src/web/DESIGN-SYSTEM.md`,
  "Generated content", has the rules.
- Use `async`, signals, or `takeUntilDestroyed`; no unmanaged subscriptions.
- Never use `any`; decode unknown data at boundaries.

## Styling and accessibility

- Import `tokens.scss`, `themes.scss`, and `global.scss` once and in that order.
- Stylesheets are SCSS: a component's `styleUrl` names a `.scss` file, inline `styles` compile as SCSS
  (`inlineStyleLanguage`), and the CLI generates `.scss`. Design tokens stay `--cp-*` custom properties, never Sass
  variables, because a theme switches at runtime through `data-cp-theme` and a Sass value is fixed at build time.
- Use semantic `--cp-*` tokens; literal theme colors in feature code are defects.
- Preserve visible focus, semantic HTML, accessible names, adjacent field errors, and status conveyed by more than color.
- Target WCAG 2.2 AA, 40px minimum touch targets, 200% zoom, narrow layouts, and reduced motion.

## Verification

From `src/web/`, run `npm ci`, `npm run build`, and `npm test`. `npm test` runs the page-contract check before
the specs; `npm run check:page-contract` runs it alone. Public UI changes also update the showcase, `README.md`, `DESIGN-SYSTEM.md`, and `projects/creator-pantry-ui/src/public-api.ts` as appropriate.

