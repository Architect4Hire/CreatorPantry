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

### The landing page's tour screenshots

The product tour on the public landing page shows real screens, photographed from a running product with
`npm run capture:tour` (`scripts/capture-tour.mjs`). It signs in, visits each panel's screen in both themes at
1280×800, writes AVIF and WebP at 1x and 2x into `public/images/tour/`, and rewrites the generated
`src/app/features/landing/landing-tour.captures.ts`. The tour shows the panels that file lists and nothing else,
so before the first capture the section is simply absent from the page.

```bash
CAPTURE_BASE_URL=https://localhost:<gateway port> \
CAPTURE_EMAIL=you@example.com CAPTURE_PASSWORD=... \
npm run capture:tour
```

It photographs the sample workspace named in the script's `SAMPLE_WORKSPACES` — `maple-thyme`, which a dedicated
capture account owns, so no real creator's work reaches the public page — and refuses any other. It also refuses
a screen that does not visibly show the thing its panel promises, so a panel whose AI work has not been done in that workspace fails by name
rather than shipping a picture of an empty product. The AI work itself — the concepts, pictures, posts, outlines
and voice guide — costs credits and a configured provider, so it is done once by hand before the run; a screen
that lives at an address no script can guess is passed in as, for example, `CAPTURE_URL_LONG_FORM`.

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
- `CpCardComponent`: standard, interactive, flush and `featured` surfaces — `featured` (a bare attribute works) is the one topic on a page that matters most: the tool's tint mixed with the sheet and an edge in the tool's colour. At most one per page
- `CpBadgeComponent`: semantic status and category tones
- `CpFieldComponent`: form label, hint, required, error and warning treatment. A `warning` is advice shown beside the control with a triangle glyph and "Heads up:" for a screen reader; it never marks the control invalid. The projected control is wired up after each render — `aria-describedby` to the hint, error and warning, `aria-invalid` while an error shows, `aria-required` when required — because a template binding cannot reach projected content, and a `*` nobody hears is decoration. A consumer's own `aria-describedby` is merged, not replaced
- `CpProgressComponent`: bounded accessible progress indicator. `valueText` replaces the percentage figure and is announced as `aria-valuetext`, for a meter whose reading is an amount rather than a fraction; `tone` (`default | success | warning | error`) colours the fill and carries no meaning of its own — pair it with a `CpStatusPillComponent` so the state is never in colour alone
- `CpDialogComponent`: modal shell with backdrop dismissal and focus target. Tab stays inside it, the page does not scroll underneath, and focus returns to what opened it unless the consumer has already moved it
- `CpDrawerComponent`: a panel that slides in from the right over a scrim and holds one task while the page stays in view. `heading` (required), an optional `description`, `size` (`standard | wide`, from `--cp-drawer-width` and `--cp-drawer-width-wide`), a default slot for a body that scrolls on its own, and `[cpDrawerActions]` for a footer that takes no room when empty. Modal: focus moves in, is trapped, and returns; the rest of the page is `inert`; body scroll is locked and restored exactly. Escape, the scrim and the close control emit `closeRequested` (`'escape' | 'scrim' | 'close-button'`) and do not close it — the consumer sets `open`, after confirming a discard if there is one. A full-screen sheet at `40rem` and narrower. While open it lives under `<body>`, so a spec queries the document. `DESIGN-SYSTEM.md` has the rule for choosing between a drawer, a dialog and a page
- `CpLightboxComponent`: one picture shown as large as the window allows and never larger than it was made. `heading` (required), an optional `description` for facts about the picture, a default slot for the consumer's own `<img>` (or its own component that loads the bytes — the lightbox takes no URL), `[cpLightboxCaption]` under the picture and `[cpLightboxActions]` for a footer, both taking no room when empty. Modal the way `cp-dialog` is: focus moves in, is trapped, and returns; body scroll is locked. Escape, the scrim and the close control emit `closeRequested` and do not close it — the consumer sets `open`. Fills the window at `40rem` and narrower. It knows one picture: moving through a set is the consumer's
- `CpQuickActionComponent`: branded creator-tool action card
- `CpStatusPillComponent`: semantic status pill (`neutral | progress | success | warning | error | stale`) with a distinct glyph per tone plus required text — meaning never relies on color alone
- `CpListShellComponent`: domain-neutral table/list frame with heading association, actions/pagination slots, and loading, error, empty, and ready states. While loading it draws skeletons (`loadingLayout`: `rows`, default, or `cards`) with one visually hidden status sentence; while not ready its pagination footer is said to a screen reader but not shown
- `CpEnterDirective` (`[cpEnter]`): what has just arrived, fading in and rising `--cp-enter-distance` over `--cp-duration-enter`, once. Marks the element `data-cp-enter`, which `global.css`'s `cp-enter` keyframes are keyed on; everything created in one change-detection pass is one arrival and lands in creation order, `--cp-enter-stagger` apart, capped at five steps. Nothing moves and nothing waits under reduced motion, and nothing is hidden without the stylesheet or on paper. Focus arriving ends it early, synchronously through `:focus-within` and then for good in the directive. Nothing re-animates: the input is read once, and the mark comes off when the animation ends *or is cancelled*, so a `[hidden]` panel does not arrive again when it is shown. `[cpEnter]="false"` writes nothing. The library applies it (a list shell's ready content, a generated result, a group of options); no page does today, and the page contract's `enter` rule guards the tokens, the keyframes and `data-cp-enter` rather than the directive
- `CpSkeletonComponent`: the shape of what is loading — `line`, `block`, `circle` or `card` (a media-card silhouette, laid out as a card grid), `count` of them; `aria-hidden`, a background-position shimmer from the skeleton tokens, still under reduced motion
- `CpTabsComponent` / `CpTabPanelComponent`: keyboard-accessible tabs (ARIA tabs pattern, roving tabindex, disabled tabs) with lazy-mounted panels
- `CpToolbarComponent`: responsive toolbar with search/filters/actions/overflow slots, loading and disabled states, and roving-tabindex keyboard navigation — which yields the arrow, `Home` and `End` keys to a focused text input, `<select>`, `<textarea>` or contenteditable, so a control in the search slot keeps its own caret and value behaviour. The "More actions" disclosure renders only when `[cpToolbarOverflow]` has content, so a toolbar that uses the first three slots shows no empty overflow button
- `CpEmptyStateComponent`: title/description, an `illustration` from the drawn set (or the older `icon` glyph, which an illustration overrides), a projected actions slot and `first-use`/`no-results` variants; drawn, a first use is roomy and takes the page tone, a no-results stays compact
- `CpIllustrationComponent` (`cp-illustration`): the drawn set — `recipe`, `picture`, `post`, `article`, `calendar`, `board`, `prompt`, `brand`, `search`, `sparkle` — as inline SVG line art in `currentColor`, `aria-hidden`, sized by `--cp-illustration-size` (7rem)
- `CpUploaderComponent`: uploader shell (browse + drag/drop) with per-item queued/uploading/success/error rendering, progress, retry, cancel, and remove — no upload transport of its own
- `CpDiffLegendComponent`: accessible legend for diff/proposal states (`added | removed | changed | moved | unchanged | warning | selected`), plus `cpDiffGlyph(kind)` and `cpDiffLabel(kind)` so a surface that marks up individual changes prints the same glyph and wording the legend beside it explains
- `CpComboboxComponent`: type-ahead combobox over a supplied option list — full WAI-ARIA combobox keyboard and `aria-activedescendant` wiring, an announced result count, `restricted` and `free-text` modes in one component, and `[filterLocally]="false"` for a consumer that filters server-side. Fetches nothing, and composes inside `cp-field` rather than rendering its own label. A caret marks it as a picker and a click opens the list, so it does not read as a plain text field before anything is typed; focus alone does not open it
- `CpCheckboxComponent`: labelled checkbox with a drawn control — the native input stays focusable and announced, checked reads as a glyph rather than a colour, the whole label is a 40px target, and both themes come from tokens. Replaces the bare native control, which the inherited `color-scheme` paints as a dark filled box in dark mode
- `CpAnchorNavComponent`: vertical list of in-page destinations (label plus optional detail) for a long form or document — ordinary tabbable links, not a tablist; it emits the activated item and leaves scrolling and focus to the consumer, renders nothing when given no items, and stacks into a gutter through `--cp-anchor-nav-direction`
- `CpFormSectionComponent`: one named part of a form — heading, optional one-sentence intro, optional server `problem` about the whole part, and the projected fields. It names its own region, describes the section by the problem while one is showing so a revealed refusal reads the heading and then the reason, and is `tabindex="-1"` for a form that focuses it. Fields projected directly into it take the `--cp-measure-field` measure; a field inside a component projected there is left to that component
- `CpFieldRowComponent`: a line of short fields that share the width instead of each taking a row, wrapping to as many columns as fit. `minColumn` widens the floor for fields that need room and `columns` is opt-in and holds the row to the form's measure in that many equal columns, which is what a row of quantities wants — each is the same share of the line in every row that uses the count, and a child with `cp-span-2` or `cp-span-3` takes that many, and one with `cp-stack-narrow` takes the whole row on a narrow screen — and its fields take the `--cp-measure-field` measure too — two wide fields splitting a desktop row between them land well past a comfortable line otherwise
- `CpChoiceGroupComponent`: a set of answers as selectable tiles rather than bare radio rows. The whole tile is the target, which is what keeps an option carrying a label, a hint and a sample sentence scannable and over 40px without each feature inventing padding. One answer or several (`multiple`), an optional `max` that disables the unpicked rest at the cap instead of dropping a pick, `layout="fit"` for short options that should wrap rather than stack, and `idPrefix` so a consumer can address one option's control. Drawn for the same reason `CpCheckboxComponent` is, and the real input stays in the DOM, so arrow-key movement, `Space` and screen-reader output are all still the browser's
- `CpNoticeComponent`: one sentence the product has to say about the thing on screen — saved, refused, shelved, still working. `neutral | success | warning | error`, and every tone brings a glyph as well as a fill so the meaning never rests on telling two hues apart; `glyph` replaces it (a flag for something shelved) but cannot switch it off. The consumer sets `role="status"` or `role="alert"`, because only the consumer knows whether the sentence interrupts, and `quiet` holds the place of a live region that has to be in the DOM before it has text. Content is projected, so an inline `Reload latest` sits in the flow of the sentence it belongs to
- `CpPageHeadComponent`: the top of a page, and the owner of its one `h1` — the app shell prints none. `heading` (required), an optional `lede`, and slots that take no room when empty: `[cpPageHeadBreadcrumb]` (in a `nav` named by `breadcrumbLabel`), `[cpPageHeadBadges]` and `[cpPageHeadActions]`, plus `[cpPageHeadIcon]` (the section's own nav icon, drawn on a `--cp-tone` tile beside the title, `aria-hidden`) and `[cpPageHeadMedia]` (a detail page's own `<img>` — never a URL — in a 4:3 frame beside the title from a 34rem-wide head, above it below that; the picture itself or one wrapper around it, never a control, and its `alt` is the asset's stored alt text, or `""` only where the title already says what it shows). The icon slot holds a decorative, non-focusable icon. The rule under the head is `--cp-tone-rule`: a 2px line in the tool's colour on a toned page, the hairline elsewhere. The heading is `tabindex="-1"` and `focusHeading()` moves focus to it, for a page that has just replaced what the creator was acting on. `heading`, not `title`: a static `title` attribute would also be a tooltip
- `CpBackLinkComponent` (`a[cpBackLink]`): the link back up from a page, drawn on the consumer's own `<a>` so the library names no route — muted, 40px tall, with an arrow hidden from a screen reader. Its text is the destination ("DAM"), not "Back to…"
- `CpCardGridComponent` (`ul[cpCardGrid]`): a list of cards as a grid — as many columns as fit, one at a narrow width or 200% zoom, and a few cards keep their size rather than stretching. An attribute on the consumer's own `<ul>`, so the list, its items and its accessible name stay the feature's; `minColumn` (default `16rem`) widens the floor, and `flow="masonry"` lets each card keep its own height and packs the columns (the dashboard's topics), reading down a column before across
- `CpBoardComponent` / `CpBoardColumnComponent` (`[cpBoard]`, `cp-board-column`): a library sorted into a few named, ordered columns — side by side when they fit, below one another when they do not, never scrolling sideways. Each column is a region named by its `heading`, says its `count` in words (`countLabel`), holds the feature's own `<ul>`, and has a `[cpBoardColumnFooter]` slot. No drag handling: moving a thing, by pointer and by keyboard, is the feature's
- `CpGeneratedLabelComponent` (`cp-generated-label`): the mark that says a model made what sits beside it — a ✦ (hidden from a screen reader) and words chosen by `kind` (`words` "Drafted by AI", `picture` "Made by AI", `reading` "Read by AI"; `CP_GENERATED_LABELS`), never free text, plus an optional `provenance` the feature writes ("Venice, flux-dev"; never a prompt body). A dashed `--cp-border-control` edge and no tone. For output that already sits in a card or under a field
- `CpGeneratedComponent` (`cp-generated`): a surface for one piece of generated output — the label and `provenance` first, the projected output, then `[cpGeneratedActions]` (accept / edit / discard) last, taking no room when empty. A `role="group"` named by its label; a dashed neutral edge and no fill. Presentation only: it saves, accepts and discards nothing
- `CpMediaCardComponent`: one thing in a library, shown by its picture. The picture is projected into `[cpMediaCardPicture]` — the library never takes a URL, because the feature that owns a picture authorises and loads it — and a drawn panel stands in when there is none, so a row of cards stays even: a faint gingham on `--cp-tone-panel` holding the `illustration` named (any `cp-illustration`), hidden from a screen reader. Without a name, `glyph` (default `▣`) maps `▣ ⌘ ❝ ◆` to `picture`, `recipe`, `prompt` and `brand`, and shows any other glyph as text. Hovering or focusing the title scales the projected picture to 1.04 inside its frame, not under reduced motion. The title is the consumer's own `<a>` in `[cpMediaCardTitle]`, set in a real heading at `headingLevel` (`2 | 3 | 4`, default `3`); the card itself is not a link. Then an optional `description`, `[cpMediaCardBadges]`, a default slot for a facts list, and `[cpMediaCardActions]` last for an action that does not open the thing (a preview), which takes no room when empty. The title's ring shows on `:focus`, for a list that moves focus to a new card by script
- `CpOptionCardComponent` (`cp-option-card`): one choice among several, compared side by side in a `ul[cpCardGrid]` — `heading` at `headingLevel`, `facts` (`CpOptionFact[]`; the first three as term-and-value tags, the rest moved into the disclosure, never dropped), one-sentence `summary`, a default slot that is always shown, `[cpOptionCardDetail]` behind a native `<details>` (`detailLabel`), and one button (`chooseLabel`, named with the heading) that emits `chosen`. `selected` shows an action-colour edge, a ✓ and "Chosen"; the button then reads "Chosen", stays focusable with `aria-disabled` and emits nothing. Not a form control: a form's answer is `CpChoiceGroupComponent`
- `CpStatTileComponent` (`cp-stat-tile`): one figure at a glance — `value` (text the feature formats from the server's count; bold sans at `--cp-font-size-2xl`) and its required `label`, read as one sentence, an optional `context` sentence, a default slot for a `cp-notice` when the figure could not be read, and `[cpStatTileLink]` for the feature's own `<a>` at the foot. No arithmetic, no unit, no trend arrow
- `CpTopicCardComponent`: one topic on a page about a single thing — a `cp-card` holding a region labelled by its `heading` (`headingLevel` `2 | 3`), a body held to `--cp-measure-prose`, and `[cpTopicCardActions]` placed last and absent when empty. `wide` lifts the measure for a table or a preview; `headingId` lets the page label something else by the same heading and is generated when not given; `featured` passes to its card (at most one per page)
- `CpFactsComponent` and `CpFactComponent` (`dl[cpFacts]`, `div[cpFact]`): facts about one thing as a real description list. `layout="stacked"` (the default) puts a small muted term over its value, for a detail page; `layout="inline"` puts them on one line, for a card. Each fact takes a `label` and projects its value, so a value can be a date, a link or a list of badges; `prose` keeps a value's own line breaks
- `CpSearchFieldComponent` and `CpSelectFieldComponent` (`cp-search-field`, `cp-select-field`): a toolbar's search box and its filter or sort. Each wraps the consumer's own native control in a label that is announced but not shown, and draws it at the toolbar's 40px; the control's value, events and disabled state stay in the feature. Put `cpToolbarSearch`, `cpToolbarFilters` or `cpToolbarActions` on the element to place it in `cp-toolbar`
- `CpValidationSummaryComponent` (`cp-validation-summary`): everything wrong with a form in one place, each line a button that takes the creator to its field. Takes `issues` (`{ path, message, severity }` with `severity` one of `error | warning | info`), an optional `labelOf` function naming a path's field, `announce` (default true; false when the form's own banner is the live region), and emits `issueSelected`. Errors are headed with their count; warnings and information sit under their own heading and never read as a refusal. Severity is a glyph (disc, triangle, `i`) as well as a colour. A status region is always present and announces the error count; `focus()` moves focus to the list, waiting for the next render if it is not drawn yet. Presentation only — which issues exist, and what selecting one does, are the form's. `CpValidationIssue` and `CpValidationSeverity` are exported with it
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
