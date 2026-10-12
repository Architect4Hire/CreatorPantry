# CreatorPantry Angular Design System

A production-minded Angular 22 design-system starter based on the supplied creator-productivity dashboard. It includes a reusable library and a responsive showcase application.

## Run it

Run the whole system from the repository root with `aspire run`. Aspire installs packages, starts the
Angular development server (`web-dev`) on an assigned port, and proxies `/runtime-config.json` to
`CreatorPantry.Web`. `npm start` refuses to run without the port and web-host address Aspire provides.

Build both the library and showcase with `npm run build`; run their tests with `npm test`. `npm test` first runs
`scripts/check-page-contract.mjs` (and its own tests), which fails when a signed-in page pads or caps its own
`:host`, prints an `h1` or a header outside `cp-page-head`, or redraws a part the library owns — a card grid, a
facts list, a toolbar field, a step list, a chip, a sliding panel, or the `.cp-muted` and `.cp-actions`
utilities. Run it alone with `npm run check:page-contract`; an exemption is a listed path with a reason in that
script (see `DESIGN-SYSTEM.md`, "What the build checks"). The
publishable library is emitted to `dist/creator-pantry-ui`, and the showcase bundle to
`dist/showcase/browser`, which `dotnet publish` copies into `CreatorPantry.Web`.

In a development build, `/design-system` renders every state of every primitive (variants, sizes,
disabled, error, empty, overflow) for visual review. The route only exists when `isDevMode()` is
true, so it is absent from production builds.

## Use the library

Import the three CSS foundations once in the consuming application's global styles, in this order:

```css
@import '@creator-pantry/ui/styles/tokens.css';
@import '@creator-pantry/ui/styles/themes.css';
@import '@creator-pantry/ui/styles/global.css';
```

Then import only the standalone components a feature needs:

```ts
import { CpButtonComponent, CpCardComponent } from '@creator-pantry/ui';
```

```html
<cp-card>
  <h2>Recipe draft</h2>
  <button cpButton>Continue writing</button>
</cp-card>
```

## Included API

- `CpThemeService`: persisted light/dark/system theme preference
- `CpButtonComponent`: primary, secondary, ghost, text, and danger variants
- `CpCardComponent`: standard, interactive, and flush surfaces
- `CpBadgeComponent`: semantic status and category tones
- `CpFieldComponent`: form label, hint, required and error treatment. The projected control is wired up after each render — `aria-describedby` to the hint and the error, `aria-required` when required — because a template binding cannot reach projected content, and a `*` nobody hears is decoration. A consumer's own `aria-describedby` is merged, not replaced
- `CpProgressComponent`: bounded accessible progress indicator. `valueText` replaces the percentage figure and is announced as `aria-valuetext`, for a meter whose reading is an amount rather than a fraction; `tone` (`default | success | warning | error`) colours the fill and carries no meaning of its own — pair it with a `CpStatusPillComponent` so the state is never in colour alone
- `CpDialogComponent`: modal shell with backdrop dismissal and focus target. Tab stays inside it, the page does not scroll underneath, and focus returns to what opened it unless the consumer has already moved it
- `CpDrawerComponent`: a panel that slides in from the right over a scrim and holds one task while the page stays in view. `heading` (required), an optional `description`, `size` (`standard | wide`, from `--cp-drawer-width` and `--cp-drawer-width-wide`), a default slot for a body that scrolls on its own, and `[cpDrawerActions]` for a footer that takes no room when empty. Modal: focus moves in, is trapped, and returns; the rest of the page is `inert`; body scroll is locked and restored exactly. Escape, the scrim and the close control emit `closeRequested` (`'escape' | 'scrim' | 'close-button'`) and do not close it — the consumer sets `open`, after confirming a discard if there is one. A full-screen sheet at `40rem` and narrower. While open it lives under `<body>`, so a spec queries the document. `DESIGN-SYSTEM.md` has the rule for choosing between a drawer, a dialog and a page
- `CpQuickActionComponent`: branded creator-tool action card
- `CpStatusPillComponent`: semantic status pill (`neutral | progress | success | warning | error | stale`) with a distinct glyph per tone plus required text — meaning never relies on color alone
- `CpListShellComponent`: domain-neutral table/list frame with heading association, actions/pagination slots, and loading, error, empty, and ready states
- `CpTabsComponent` / `CpTabPanelComponent`: keyboard-accessible tabs (ARIA tabs pattern, roving tabindex, disabled tabs) with lazy-mounted panels
- `CpToolbarComponent`: responsive toolbar with search/filters/actions/overflow slots, loading and disabled states, and roving-tabindex keyboard navigation — which yields the arrow, `Home` and `End` keys to a focused text input, `<select>`, `<textarea>` or contenteditable, so a control in the search slot keeps its own caret and value behaviour. The "More actions" disclosure renders only when `[cpToolbarOverflow]` has content, so a toolbar that uses the first three slots shows no empty overflow button
- `CpEmptyStateComponent`: title/description/icon empty state with a projected actions slot and `first-use`/`no-results` variants
- `CpUploaderComponent`: uploader shell (browse + drag/drop) with per-item queued/uploading/success/error rendering, progress, retry, cancel, and remove — no upload transport of its own
- `CpDiffLegendComponent`: accessible legend for diff/proposal states (`added | removed | changed | moved | unchanged | warning | selected`), plus `cpDiffGlyph(kind)` and `cpDiffLabel(kind)` so a surface that marks up individual changes prints the same glyph and wording the legend beside it explains
- `CpComboboxComponent`: type-ahead combobox over a supplied option list — full WAI-ARIA combobox keyboard and `aria-activedescendant` wiring, an announced result count, `restricted` and `free-text` modes in one component, and `[filterLocally]="false"` for a consumer that filters server-side. Fetches nothing, and composes inside `cp-field` rather than rendering its own label. A caret marks it as a picker and a click opens the list, so it does not read as a plain text field before anything is typed; focus alone does not open it
- `CpCheckboxComponent`: labelled checkbox with a drawn control — the native input stays focusable and announced, checked reads as a glyph rather than a colour, the whole label is a 40px target, and both themes come from tokens. Replaces the bare native control, which the inherited `color-scheme` paints as a dark filled box in dark mode
- `CpAnchorNavComponent`: vertical list of in-page destinations (label plus optional detail) for a long form or document — ordinary tabbable links, not a tablist; it emits the activated item and leaves scrolling and focus to the consumer, renders nothing when given no items, and stacks into a gutter through `--cp-anchor-nav-direction`
- `CpFormSectionComponent`: one named part of a form — heading, optional one-sentence intro, optional server `problem` about the whole part, and the projected fields. It names its own region, describes the section by the problem while one is showing so a revealed refusal reads the heading and then the reason, and is `tabindex="-1"` for a form that focuses it. Fields projected directly into it take the `--cp-measure-field` measure; a field inside a component projected there is left to that component
- `CpFieldRowComponent`: a line of short fields that share the width instead of each taking a row, wrapping to as many columns as fit. `minColumn` widens the floor for fields that need room, and its fields take the `--cp-measure-field` measure too — two wide fields splitting a desktop row between them land well past a comfortable line otherwise
- `CpChoiceGroupComponent`: a set of answers as selectable tiles rather than bare radio rows. The whole tile is the target, which is what keeps an option carrying a label, a hint and a sample sentence scannable and over 40px without each feature inventing padding. One answer or several (`multiple`), an optional `max` that disables the unpicked rest at the cap instead of dropping a pick, `layout="fit"` for short options that should wrap rather than stack, and `idPrefix` so a consumer can address one option's control. Drawn for the same reason `CpCheckboxComponent` is, and the real input stays in the DOM, so arrow-key movement, `Space` and screen-reader output are all still the browser's
- `CpNoticeComponent`: one sentence the product has to say about the thing on screen — saved, refused, shelved, still working. `neutral | success | warning | error`, and every tone brings a glyph as well as a fill so the meaning never rests on telling two hues apart; `glyph` replaces it (a flag for something shelved) but cannot switch it off. The consumer sets `role="status"` or `role="alert"`, because only the consumer knows whether the sentence interrupts, and `quiet` holds the place of a live region that has to be in the DOM before it has text. Content is projected, so an inline `Reload latest` sits in the flow of the sentence it belongs to
- `CpPageHeadComponent`: the top of a page, and the owner of its one `h1` — the app shell prints none. `heading` (required), an optional `lede`, and three slots that take no room when empty: `[cpPageHeadBreadcrumb]` (in a `nav` named by `breadcrumbLabel`), `[cpPageHeadBadges]` and `[cpPageHeadActions]`. The heading is `tabindex="-1"` and `focusHeading()` moves focus to it, for a page that has just replaced what the creator was acting on. `heading`, not `title`: a static `title` attribute would also be a tooltip
- `CpBackLinkComponent` (`a[cpBackLink]`): the link back up from a page, drawn on the consumer's own `<a>` so the library names no route — muted, 40px tall, with an arrow hidden from a screen reader. Its text is the destination ("DAM"), not "Back to…"
- `CpCardGridComponent` (`ul[cpCardGrid]`): a list of cards as a grid — as many columns as fit, one at a narrow width or 200% zoom, and a few cards keep their size rather than stretching. An attribute on the consumer's own `<ul>`, so the list, its items and its accessible name stay the feature's; `minColumn` (default `16rem`) widens the floor
- `CpMediaCardComponent`: one thing in a library, shown by its picture. The picture is projected into `[cpMediaCardPicture]` — the library never takes a URL, because the feature that owns a picture authorises and loads it — and a tinted panel with a `glyph` (default `▣`, hidden from a screen reader) stands in when there is none, so a row of cards stays even. The title is the consumer's own `<a>` in `[cpMediaCardTitle]`, set in a real heading at `headingLevel` (`2 | 3 | 4`, default `3`); the card itself is not a link. Then an optional `description`, `[cpMediaCardBadges]`, a default slot for a facts list, and `[cpMediaCardActions]` last for an action that does not open the thing (a preview), which takes no room when empty. The title's ring shows on `:focus`, for a list that moves focus to a new card by script
- `CpTopicCardComponent`: one topic on a page about a single thing — a `cp-card` holding a region labelled by its `heading` (`headingLevel` `2 | 3`), a body held to `--cp-measure-prose`, and `[cpTopicCardActions]` placed last and absent when empty. `wide` lifts the measure for a table or a preview; `headingId` lets the page label something else by the same heading and is generated when not given
- `CpFactsComponent` and `CpFactComponent` (`dl[cpFacts]`, `div[cpFact]`): facts about one thing as a real description list. `layout="stacked"` (the default) puts a small muted term over its value, for a detail page; `layout="inline"` puts them on one line, for a card. Each fact takes a `label` and projects its value, so a value can be a date, a link or a list of badges; `prose` keeps a value's own line breaks
- `CpSearchFieldComponent` and `CpSelectFieldComponent` (`cp-search-field`, `cp-select-field`): a toolbar's search box and its filter or sort. Each wraps the consumer's own native control in a label that is announced but not shown, and draws it at the toolbar's 40px; the control's value, events and disabled state stay in the feature. Put `cpToolbarSearch`, `cpToolbarFilters` or `cpToolbarActions` on the element to place it in `cp-toolbar`
- `CpStepperComponent` (`cp-stepper`): the steps of a flow, in order, saying where the creator is. Takes a required `label` (the list's accessible name) and `steps` — `{ id, label, status }` with `status` one of `upcoming | current | done | skipped`. Each row shows the step's name, its state in a word and a glyph hidden from a screen reader; the current row carries `aria-current="step"`. A read-out with no outputs: rows are not links, and a flow moves by its own Back and Continue. Renders nothing for an empty list. `CpStep` and `CpStepStatus` are exported with it
- `CpChipGroupComponent` and `CpChipComponent` (`[cpChipGroup]`, `button[cpChip]`): a filter with a few fixed answers, drawn as a row of toggles. The group is an attribute on the consumer's own element — so it can also carry `cpToolbarFilters` — takes a required `label`, and is announced as one named group; it wraps. A chip is the consumer's own `<button>`: `pressed` sets `aria-pressed` and the pressed look (border, fill and a heavier label, so the state is not colour alone), and `(click)` stays the consumer's, which is how one chip serves a filter where several may be on and one where exactly one is. 40px tall; `disabled` is the native attribute
- `CpToastRegionComponent`: polite/assertive toast region with severity-based auto-dismiss timing, hover/focus pause, deduplication, and persistent warning/error toasts
- Semantic CSS tokens for color, typography, spacing, radii, elevation, and motion

## Guardrails

- Feature code uses `--cp-*` semantic tokens; never copy literal colors from the showcase.
- Components stay domain-neutral. Recipe-specific data and workflows belong in features.
- Use native semantic elements first. Every icon-only control needs an accessible name.
- Keep all components standalone, signal-based, strictly typed, and `OnPush`.
- Test light, dark, keyboard, 200% zoom, narrow mobile, and reduced-motion behavior before merging.

See `DESIGN-SYSTEM.md` for visual decisions and `CLAUDE.md` for Claude Code instructions.
