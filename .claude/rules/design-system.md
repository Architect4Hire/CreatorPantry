# CreatorPantry Angular Design System

The installed Angular 22 design system is the visual source of truth. Do not create a parallel token set or shared-component library.

## Authoritative paths

- Direction and accessibility: `src/web/DESIGN-SYSTEM.md`
- Workspace instructions: `src/web/CLAUDE.md`
- Library: `src/web/projects/creator-pantry-ui/`
- Public API: `src/web/projects/creator-pantry-ui/src/public-api.ts`
- Tokens: `src/web/projects/creator-pantry-ui/src/lib/styles/tokens.scss`
- Themes: `src/web/projects/creator-pantry-ui/src/lib/styles/themes.scss`
- Global styles: `src/web/projects/creator-pantry-ui/src/lib/styles/global.scss`
- Showcase: `src/web/src/app/`

## Visual contract

CreatorPantry should feel like a food blog with a working tool underneath: editorial warmth over productivity-tool density. `Fredoka`, a rounded display sans, sets every heading through `--cp-font-display`; `DM Sans` is used for controls, labels, content, and data. Surfaces are paper and warm ink with hairline edges in light, and neutral charcoal greys in dark (a `#202020` page; never tinted); a sheet — card, dialog, lightbox — is edged with `--cp-card-edge`, and every control a creator types into or picks from (field, select, checkbox, radio mark, secondary button) with `--cp-border-control` (3:1 on every surface). Evergreen is the primary action color. Pink, orange, purple, and blue distinguish tools and secondary states without competing with primary actions.

Theme is applied with `data-cp-theme="light|dark"` on `<html>`. `CpThemeService` manages system preference and persistence. Never create separate light/dark component markup.

A tool's tone is applied with `data-cp-tone="purple|pink|blue|orange"`, set by the route's `data: { tone }` on the page frame and never by a feature (the page contract's `tone` rule): AI Recipe Studio purple, Image Studio pink, Social Studio blue, Long-form and the Content Pipeline orange, every other page none. Parts draw with `--cp-tone`, `--cp-tone-soft`, `--cp-tone-text` and `--cp-tone-on`, which are the primary's when no tone is set. A tone marks the tool, never the primary action or a status. An accent as text is `--cp-<accent>-text`, never the raw accent (light orange is darker as text). Loading draws `--cp-skeleton-base`/`--cp-skeleton-shine`; entrance motion is `cpEnter`'s, from `--cp-duration-enter`, `--cp-enter-distance` and `--cp-enter-stagger`. `src/web/DESIGN-SYSTEM.md`, "Warmth inside the app", has the rules.

Consumers import the CSS foundation exactly once, in this order:

```scss
@use "@creator-pantry/ui/styles/tokens";
@use "@creator-pantry/ui/styles/themes";
@use "@creator-pantry/ui/styles/global";
```

## Implemented public API

- `CpThemeService`
- `CpButtonComponent`: `primary | secondary | ghost | text | danger`; `sm | md | lg`; optional full width
- `CpCardComponent`: standard, interactive, flush and `featured` surfaces. `featured` (also on `cp-topic-card`) is the one topic on a page that matters most — the tool's tint mixed with the sheet, edged in the tool's colour — and a page has at most one
- `CpBadgeComponent`: `neutral | success | pink | orange | purple | blue`
- `CpFieldComponent`: label, hint, required, error, warning, projected native control; wires `aria-describedby` (hint, error, warning), `aria-invalid` while an error shows and `aria-required` onto that control so the hint and the `*` are not visual only. A warning is advice and never marks the control invalid
- `CpProgressComponent`: labeled bounded progress
- `CpDialogComponent`: modal shell, title/description, action projection, close event; traps Tab, locks page scroll, returns focus
- `CpDrawerComponent`: one task beside the page it is about — slides in from the right over a scrim, `heading`, optional `description`, `standard | wide`, a body that scrolls on its own, `[cpDrawerActions]` footer. Modal with the page `inert`; focus trapped and returned; a full-screen sheet at `40rem` and narrower. Escape, the scrim and the close control emit `closeRequested` and never close it, so a consumer can confirm a discard through `ConfirmService` first. Moves itself to `<body>` while open. Not a dialog variant: `DESIGN-SYSTEM.md`, "A drawer, a dialog or a page", says which to use
- `CpLightboxComponent`: one picture shown large — `heading`, optional `description`, the consumer's own projected `<img>` held to the window and to its own pixels, `[cpLightboxCaption]` and `[cpLightboxActions]`. Never takes a URL: the feature authorises and loads its own bytes. Modal like `cp-dialog`; Escape, the scrim and the close control emit `closeRequested` and never close it. Knows one picture — moving through a set is the consumer's. Opened from a "View larger" button beside the picture, not from the picture. Use it rather than a `cp-dialog` with a picture in it
- `CpQuickActionComponent`: creator-tool action card with tone and activation event
- `CpStatusPillComponent`: `neutral | progress | success | warning | error | stale`, distinct glyph per tone plus required text
- `CpListShellComponent`: domain-neutral list/table frame — heading, actions/pagination slots, loading/error/empty/ready states; no columns or data. Loading draws `cp-skeleton` in `loadingLayout` `rows` (default) or `cards`, or a projected `[cpListShellLoading]` (a board's columns), with one visually hidden status sentence; the ready content scrolls in a positioned region, so a `.cp-sr-only` table header cannot widen the page; while not ready the pagination footer is offscreen (said, not shown)
- `CpEnterDirective` (`[cpEnter]`): what has just arrived, fading in and rising `--cp-enter-distance` over `--cp-duration-enter`, once. It marks the element `data-cp-enter` and `global.css` holds the `cp-enter` keyframes; everything created in one change-detection pass is one arrival, landing in creation order `--cp-enter-stagger` apart, capped at five steps. Nothing moves or waits under reduced motion, nothing is hidden without the sheet or on paper, focus arriving ends it early (`:focus-within` synchronously, then the directive), and nothing re-animates — a cancelled animation clears the mark too. The library applies it — a `cp-list-shell`'s ready content, a `cp-generated` result, a group of `cp-option-card`s; no page does today, and the page contract's `enter` rule guards the entrance tokens, the `cp-enter` keyframes and `data-cp-enter`, not the directive itself
- `CpSkeletonComponent`: what is loading, drawn as `line`, `block`, `circle` or `card` (a media-card silhouette, as a card grid), `count` of them. `aria-hidden` — the loading region owns `aria-busy` and its one status sentence; shimmers by `background-position`, still under reduced motion. Use it rather than a "Loading…" sentence or a `.skeleton` in a feature stylesheet
- `CpTabsComponent` / `CpTabPanelComponent`: ARIA tabs pattern, roving tabindex, disabled tabs, lazy-mounted panels
- `CpToolbarComponent`: search/filters/actions/overflow slots, loading/disabled states, roving-tabindex keyboard nav
- `CpEmptyStateComponent`: title/description, `illustration` (one of the drawn set; wins over the older `icon` glyph), projected actions slot, `first-use | no-results` variants. Drawn, a first use is the roomy layout in `--cp-tone-text`; no-results stays compact and uses `search`
- `CpIllustrationComponent` (`cp-illustration`): the drawn set — `recipe`, `picture`, `post`, `article`, `calendar`, `board`, `prompt`, `brand`, `search`, `sparkle` — inline SVG in `currentColor`, `aria-hidden`, `--cp-illustration-size` (7rem). Drawn for this library; no claims on food. Use it rather than an `.illustration` or a drawing in a feature
- `CpUploaderComponent`: browse/drag-drop shell, per-item queued/uploading/success/error rendering, retry/cancel/remove — no transport of its own
- `CpDiffLegendComponent`: legend for `added | removed | changed | moved | unchanged | warning | selected`; presentation only, no diff computation
- `CpToastRegionComponent`: polite/assertive toast region, auto-dismiss with hover/focus pause, dedup, persistent warning/error
- `CpComboboxComponent`: type-ahead over a supplied list — WAI-ARIA combobox keyboard, `aria-activedescendant`, announced result count, `restricted`/`free-text` modes, `filterLocally` for server-filtered lists. No fetching, no domain vocabulary; composes inside `cp-field`
- `CpCheckboxComponent`: labelled checkbox with a drawn control — glyph for checked, 40px label target, token-driven in both themes; the native input stays focusable and announced. Use it rather than a bare `input[type=checkbox]`, which the theme's `color-scheme` paints as a dark filled box in dark mode
- `CpAnchorNavComponent`: in-page destination nav (label + optional detail); tabbable links, not a tablist; emits the activated item and leaves scrolling and focus to the consumer; renders nothing when empty
- `CpFormSectionComponent`: one named part of a form — heading, optional intro, optional server `problem` about the whole part, projected fields. Names its own region, describes the section by the problem while one shows, `tabindex="-1"` for a form that reveals it, and caps directly projected fields at `--cp-measure-field`
- `CpFieldRowComponent`: a line of short fields sharing the width, wrapping to as many columns as fit; `minColumn` widens the floor and `columns` holds the row to the measure in equal columns (a row of quantities; opt-in)
- `CpChoiceGroupComponent`: a set of answers as selectable tiles — one answer or several, an optional `max` that disables the rest at the cap, `layout="fit"` for short options, label/hint/example per option. Drawn like `CpCheckboxComponent` and for the same reason; the real input stays focusable and announced, so arrow keys and `Space` remain the browser's. Use it rather than hand-drawing radio rows in a feature stylesheet
- `CpNoticeComponent`: one sentence about the thing on screen — `neutral | success | warning | error`, each with a glyph as well as a fill, a replaceable `glyph`, and `quiet` for a live region that must exist before it has text. The consumer owns `role`; content is projected, so an inline action sits in the sentence it belongs to
- `CpGeneratedLabelComponent` / `CpGeneratedComponent` (`cp-generated-label`, `cp-generated`): what a model made, said in words picked by `kind` — "Drafted by AI", "Made by AI", "Read by AI" — beside a ✦, with an optional `provenance` line the feature writes (never a prompt body). The label alone where the output already has an edge; the surface, a group named by its label with `[cpGeneratedActions]` last, where it has none. Dashed neutral edge, no tone. Presentation only. Use it rather than a badge or a sentence of a feature's own, which the page contract's `generated` rule fails
- `CpPageHeadComponent`: the top of a page and the owner of its one `h1` (the shell prints none) — `heading`, optional `lede`, slots for a breadcrumb, badges, the primary action, the section's icon (`[cpPageHeadIcon]`, on a `--cp-tone` tile) and a detail page's own picture (`[cpPageHeadMedia]`, the consumer's `<img>`, 4:3, beside the title when wide) that take no room when empty; the rule under it is `--cp-tone-rule`; on paper the picture and the tile are left off and the title takes the full width; `focusHeading()` for a page that has just replaced what the creator was acting on. The title is `--cp-font-size-title` on every page
- `CpBackLinkComponent` (`a[cpBackLink]`): the breadcrumb link, on the consumer's own `<a>` so the library names no route; its text is the destination, not "Back to…"
- `CpCardGridComponent` (`ul[cpCardGrid]`): a list of cards as a grid — as many columns as fit, one at a narrow width; `minColumn` widens the floor, and `flow="masonry"` lets each card keep its own height and packs the columns, for cards whose order is a ranking rather than a sequence (the dashboard's topics). Use it rather than a `.grid` in a feature stylesheet
- `CpBoardComponent` / `CpBoardColumnComponent` (`[cpBoard]`, `cp-board-column`): a library sorted into a few named, ordered columns — side by side when they fit, below one another when they do not, never scrolling sideways. Each column is a region named by its `heading`, says its `count` in words (`countLabel`), takes the feature's own `<ul>` and a `[cpBoardColumnFooter]`. No drag handling: moving a thing, by pointer and by keyboard, is the feature's. Use it rather than a `.board` or `.lane` in a feature stylesheet
- `CpMediaCardComponent`: one thing in a library shown by its picture — projected picture (when there is none, or while the projected element is marked `data-cp-picture-pending`, a gingham panel on `--cp-tone-panel` with the `illustration` named, or the one `glyph` maps to; the picture leans in on the title's hover or focus, still under reduced motion), the consumer's own link as the title at `headingLevel`, `description`, badges, a default slot for facts, and `[cpMediaCardActions]` last for an action that does not open the thing. Never takes a URL: the feature authorises and loads its own bytes
- `CpOptionCardComponent` (`cp-option-card`): one choice among several compared side by side in `ul[cpCardGrid]` — heading, up to three key `facts` as tags (more move into the disclosure, never dropped), a `summary`, an always-shown default slot, `[cpOptionCardDetail]` behind a native disclosure, and one button named with the heading that emits `chosen`. `selected` is an edge, a ✓ and "Chosen", with the button left focusable and `aria-disabled`. An action, not a form value: a form's answer is `CpChoiceGroupComponent`. The page contract's `option-card` rule fails a feature that draws its own
- `CpStatTileComponent` (`cp-stat-tile`): one figure read at a glance — the server's count as the feature formats it (`value`), what it counts in words (`label`), read as one sentence and never a bare number, an optional `context` sentence and `[cpStatTileLink]`. Bold sans, ink, no arithmetic, no unit, no trend arrow unless the server sends the comparison. Sized by the tile, `--cp-font-size-2xl` its ceiling. The page contract's `stat-tile` rule fails a feature that draws its own
- `CpTopicCardComponent`: one topic on a detail page — a `cp-card` holding a region named by its `heading`, a body held to `--cp-measure-prose` (`wide` lifts it for a table), and an actions slot placed last. `headingId` lets the page label something else by the same heading; `featured` passes to its card
- `CpFactsComponent` / `CpFactComponent` (`dl[cpFacts]`, `div[cpFact]`): term-and-value pairs as a real description list — `stacked` on a detail page, `inline` on a card, `across` for a strip of a few short figures side by side (a recipe's yield and times under its head); each value is projected, and `prose` keeps the creator's line breaks. Use it rather than a `.facts` in a feature stylesheet
- `CpSearchFieldComponent` / `CpSelectFieldComponent`: a toolbar's search box and its filter or sort — the consumer's own native `<input type="search">` or `<select>`, named by a `label` that is announced but not shown, drawn at 40px. No fetching; value and events stay in the feature
- `CpValidationSummaryComponent`: every problem with a form in one place, each a button that takes the creator to its field — `issues` of `{ path, message, severity: error | warning | info }`, optional `labelOf(path)`, `announce` (false when the form's own banner is the live region), `issueSelected` output, `focus()`. A plain bulleted list with no heading, errors first; advice says so in a word and is not announced, so severity is never colour alone. Presentation only: the form says which issues exist and reveals the field. Fed from `FormValidator` (`src/app/core/forms/`), which is where client rules, warnings and the server's `fieldErrors` meet
- `CpStepperComponent`: the steps of a flow in order — `label`, `steps` of `{ id, label, status }` (`upcoming | current | done | skipped`); each state said in a word and a glyph, the current row `aria-current="step"`. A read-out, not navigation: no outputs, and the consumer owns the `<nav>` around it. Use it rather than a `.step-list` in a feature stylesheet
- `CpChipGroupComponent` / `CpChipComponent` (`[cpChipGroup]`, `button[cpChip]`): a filter with a few fixed answers as a named row of toggles — `pressed` sets `aria-pressed` and a look that is not colour alone; `(click)` and whether one or several may be on stay the consumer's. Use it rather than a `.chip` in a feature stylesheet; a filter with many or server-read answers is a `cp-select-field`

Use these exports from `@creator-pantry/ui`; deep imports from `src/lib` are defects.

## Extension rules

1. Classify work as token, primitive, reusable component, pattern, or feature composition.
2. Search the public API and existing source before adding anything.
3. Prefer composition over variant growth.
4. Define selector, typed signal inputs/outputs, content projection, ARIA, states, and responsive behavior first.
5. Reusable, domain-neutral UI belongs in `projects/creator-pantry-ui`; recipe/content workflows remain in feature folders.
6. Use semantic `--cp-*` tokens. If a semantic intent is missing, add it to both themes.
7. Update the showcase, documentation, and `public-api.ts` for public changes.
8. Run `npm test` from `src/web/`: `scripts/check-page-contract.mjs` runs first and fails on a page that redraws a part listed above. A new part that features were drawing privately gets a rule there in the same change.
9. Verify WCAG 2.2 AA, keyboard operation, visible focus, screen-reader naming, 200% zoom, narrow layout, long content, both themes, and reduced motion.

Generated `dist/` output and `node_modules/` are never committed.
