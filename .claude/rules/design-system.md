# CreatorPantry Angular Design System

The installed Angular 22 design system is the visual source of truth. Do not create a parallel token set or shared-component library.

## Authoritative paths

- Direction and accessibility: `src/web/DESIGN-SYSTEM.md`
- Workspace instructions: `src/web/CLAUDE.md`
- Library: `src/web/projects/creator-pantry-ui/`
- Public API: `src/web/projects/creator-pantry-ui/src/public-api.ts`
- Tokens: `src/web/projects/creator-pantry-ui/src/lib/styles/tokens.css`
- Themes: `src/web/projects/creator-pantry-ui/src/lib/styles/themes.css`
- Global styles: `src/web/projects/creator-pantry-ui/src/lib/styles/global.css`
- Showcase: `src/web/src/app/`

## Visual contract

CreatorPantry pairs editorial warmth with productivity-tool density. `Fredoka`, a rounded display sans, is used for editorial/display moments; `DM Sans` is used for controls, labels, content, and data. Evergreen is the primary action color. Pink, orange, purple, and blue distinguish tools and secondary states without competing with primary actions.

Theme is applied with `data-cp-theme="light|dark"` on `<html>`. `CpThemeService` manages system preference and persistence. Never create separate light/dark component markup.

Consumers import the CSS foundation exactly once, in this order:

```css
@import '@creator-pantry/ui/styles/tokens.css';
@import '@creator-pantry/ui/styles/themes.css';
@import '@creator-pantry/ui/styles/global.css';
```

## Implemented public API

- `CpThemeService`
- `CpButtonComponent`: `primary | secondary | ghost | text | danger`; `sm | md | lg`; optional full width
- `CpCardComponent`: standard, interactive, and flush surfaces
- `CpBadgeComponent`: `neutral | success | pink | orange | purple | blue`
- `CpFieldComponent`: label, hint, required, error, projected native control; wires `aria-describedby` and `aria-required` onto that control so the hint and the `*` are not visual only
- `CpProgressComponent`: labeled bounded progress
- `CpDialogComponent`: modal shell, title/description, action projection, close event; traps Tab, locks page scroll, returns focus
- `CpDrawerComponent`: one task beside the page it is about — slides in from the right over a scrim, `heading`, optional `description`, `standard | wide`, a body that scrolls on its own, `[cpDrawerActions]` footer. Modal with the page `inert`; focus trapped and returned; a full-screen sheet at `40rem` and narrower. Escape, the scrim and the close control emit `closeRequested` and never close it, so a consumer can confirm a discard through `ConfirmService` first. Moves itself to `<body>` while open. Not a dialog variant: `DESIGN-SYSTEM.md`, "A drawer, a dialog or a page", says which to use
- `CpQuickActionComponent`: creator-tool action card with tone and activation event
- `CpStatusPillComponent`: `neutral | progress | success | warning | error | stale`, distinct glyph per tone plus required text
- `CpListShellComponent`: domain-neutral list/table frame — heading, actions/pagination slots, loading/error/empty/ready states; no columns or data
- `CpTabsComponent` / `CpTabPanelComponent`: ARIA tabs pattern, roving tabindex, disabled tabs, lazy-mounted panels
- `CpToolbarComponent`: search/filters/actions/overflow slots, loading/disabled states, roving-tabindex keyboard nav
- `CpEmptyStateComponent`: title/description/icon, projected actions slot, `first-use | no-results` variants
- `CpUploaderComponent`: browse/drag-drop shell, per-item queued/uploading/success/error rendering, retry/cancel/remove — no transport of its own
- `CpDiffLegendComponent`: legend for `added | removed | changed | moved | unchanged | warning | selected`; presentation only, no diff computation
- `CpToastRegionComponent`: polite/assertive toast region, auto-dismiss with hover/focus pause, dedup, persistent warning/error
- `CpComboboxComponent`: type-ahead over a supplied list — WAI-ARIA combobox keyboard, `aria-activedescendant`, announced result count, `restricted`/`free-text` modes, `filterLocally` for server-filtered lists. No fetching, no domain vocabulary; composes inside `cp-field`
- `CpCheckboxComponent`: labelled checkbox with a drawn control — glyph for checked, 40px label target, token-driven in both themes; the native input stays focusable and announced. Use it rather than a bare `input[type=checkbox]`, which the theme's `color-scheme` paints as a dark filled box in dark mode
- `CpAnchorNavComponent`: in-page destination nav (label + optional detail); tabbable links, not a tablist; emits the activated item and leaves scrolling and focus to the consumer; renders nothing when empty
- `CpFormSectionComponent`: one named part of a form — heading, optional intro, optional server `problem` about the whole part, projected fields. Names its own region, describes the section by the problem while one shows, `tabindex="-1"` for a form that reveals it, and caps directly projected fields at `--cp-measure-field`
- `CpFieldRowComponent`: a line of short fields sharing the width, wrapping to as many columns as fit; `minColumn` widens the floor
- `CpChoiceGroupComponent`: a set of answers as selectable tiles — one answer or several, an optional `max` that disables the rest at the cap, `layout="fit"` for short options, label/hint/example per option. Drawn like `CpCheckboxComponent` and for the same reason; the real input stays focusable and announced, so arrow keys and `Space` remain the browser's. Use it rather than hand-drawing radio rows in a feature stylesheet
- `CpNoticeComponent`: one sentence about the thing on screen — `neutral | success | warning | error`, each with a glyph as well as a fill, a replaceable `glyph`, and `quiet` for a live region that must exist before it has text. The consumer owns `role`; content is projected, so an inline action sits in the sentence it belongs to
- `CpPageHeadComponent`: the top of a page and the owner of its one `h1` (the shell prints none) — `heading`, optional `lede`, slots for a breadcrumb, badges and the primary action that take no room when empty; `focusHeading()` for a page that has just replaced what the creator was acting on. The title is `--cp-font-size-title` on every page
- `CpBackLinkComponent` (`a[cpBackLink]`): the breadcrumb link, on the consumer's own `<a>` so the library names no route; its text is the destination, not "Back to…"
- `CpCardGridComponent` (`ul[cpCardGrid]`): a list of cards as a grid — as many columns as fit, one at a narrow width; `minColumn` widens the floor. Use it rather than a `.grid` in a feature stylesheet
- `CpMediaCardComponent`: one thing in a library shown by its picture — projected picture (a tinted panel and `glyph` when there is none), the consumer's own link as the title at `headingLevel`, `description`, badges, a default slot for facts, and `[cpMediaCardActions]` last for an action that does not open the thing. Never takes a URL: the feature authorises and loads its own bytes
- `CpTopicCardComponent`: one topic on a detail page — a `cp-card` holding a region named by its `heading`, a body held to `--cp-measure-prose` (`wide` lifts it for a table), and an actions slot placed last. `headingId` lets the page label something else by the same heading
- `CpFactsComponent` / `CpFactComponent` (`dl[cpFacts]`, `div[cpFact]`): term-and-value pairs as a real description list — `stacked` on a detail page, `inline` on a card; each value is projected, and `prose` keeps the creator's line breaks. Use it rather than a `.facts` in a feature stylesheet
- `CpSearchFieldComponent` / `CpSelectFieldComponent`: a toolbar's search box and its filter or sort — the consumer's own native `<input type="search">` or `<select>`, named by a `label` that is announced but not shown, drawn at 40px. No fetching; value and events stay in the feature
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

