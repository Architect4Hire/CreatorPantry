# CreatorPantry visual system

## Direction

CreatorPantry is a focused production workspace for food bloggers and content creators—not a recipe index. The interface pairs the brand's warm, hand-in-the-kitchen energy with productivity-tool density: bold and rounded where the [logo](public/images/logo.png) is bold and rounded, vivid where a pantry of fresh produce is vivid, never flattened into a muted corporate palette. Fredoka gives content moments the same friendly, chunky display voice as the wordmark; DM Sans keeps controls and data highly legible. Evergreen (drawn from the mixing bowl in the mark) is the action color. Pink, orange, purple, and blue—each saturated enough to read as fresh tomato, carrot, eggplant, and blueberry rather than a desaturated tint—distinguish tools and states without competing with primary actions.

## Layers

1. Primitive tokens define scale: spacing, type, radii, motion, z-index (`--cp-z-topbar` < `--cp-z-sidebar` < `--cp-z-overlay` < `--cp-z-modal`).
2. Semantic theme tokens define intent: background, surface, text, border, primary, status, `--cp-ink-on-accent` (readable text/icon color for any solid accent background), `--cp-scrim` (modal backdrop).
3. Angular primitives implement interaction and accessibility.
4. Feature compositions combine primitives for recipe editing, AI writing, image production, planning, and analytics.

## Theme contract

Set `data-cp-theme="light|dark"` on `<html>`. `CpThemeService` manages this attribute, respects system preference, and persists the user's explicit choice. Never create separate component markup for dark mode.

Print is always light. Browsers leave backgrounds off a printed page, so `themes.css` puts a dark theme back on the light palette under `@media print`; a feature never needs to know which theme it was printed from. The shell hides its sidebar, top bar and page title on paper, and `styles.css` sets the sheet margins with `@page`. A feature that prints lays out only its own content: sizes in points, outlines rather than fills, `break-inside: avoid` on anything that must not split across sheets, and a `screen-only` class on its own controls. `RecipeKitchenViewComponent` is the reference, and `PrintService` opens the dialog.

## Content hierarchy

- Display rounded sans (Fredoka): page moments, feature headings, editorial quotes.
- Sans bold: controls, labels, item titles, metrics.
- Muted sans: metadata and supporting copy.
- Uppercase micro-label: category or workflow context only.

## Confirmations

A confirmation — a question with two answers, one of which loses work the creator cannot recover — is a SweetAlert2 modal raised by `ConfirmService` (`src/app/core/confirm.service.ts`), never hand-built markup and never `CpDialogComponent`. `CpDialogComponent` stays the right surface for a modal that *hosts* something, such as the workspace-creation form.

The popup is appended to `<body>`, outside Angular's style encapsulation, so it is themed in `src/styles.css` through the `cp-swal*` class names with `buttonsStyling: false`. Every value there is a `--cp-*` token, which is what makes a confirmation follow `data-cp-theme` into dark mode rather than arriving in SweetAlert2's own palette. Confirm wording names the action ("Discard changes"), never "OK"; the cancelling button holds focus; and dismissing by Escape, backdrop or close always means "stay".

A browser close, refresh or full navigation cannot be confirmed this way — the browser shows its own message — so that path remains a `beforeunload` listener.

## Accessibility baseline

Target WCAG 2.2 AA. Preserve visible focus. Do not communicate status by color alone. Touch targets should be at least 40px. Dialogs require a title and close action. Form errors remain adjacent to their field and use `role="alert"`. `global.css` honors `prefers-reduced-motion: reduce` for every animation/transition; do not add motion that bypasses it.

## Meters and figures

A meter reads as an amount when the question is "how much have I got" and as a percentage when it is "how far through is this". `CpProgressComponent` does both: `valueText` replaces the percentage figure and is announced as `aria-valuetext`, so a screen reader hears the same words a sighted reader sees rather than a fraction nobody asked for.

Its `tone` colours the fill and does nothing else — it adds no glyph, changes no text and changes no ARIA. That is deliberate: a bar that only changed colour would be status by colour alone. Pair a toned meter with a `CpStatusPillComponent` naming the state in a word, and the meter's colour becomes the quieter half of something already said.

A figure is bold `--cp-font-sans`, not the display face: metrics are read, not declaimed. Put the reading measure on a wrapper free to shrink rather than on the track, where a `max-width` becomes the meter's floor instead of its ceiling.

Toolbar arrow-key navigation (`CpToolbarComponent`) yields the arrow, `Home` and `End` keys to a focused text input, `<select>`, `<textarea>` or contenteditable, so a control in its search slot keeps its own caret and value behavior.

Its "More actions" disclosure appears only when the consumer projects something into `[cpToolbarOverflow]`. A control that opens onto an empty popover is a dead end for every user and a mislabeled target for a screen reader, so an empty slot hides the disclosure entirely — which also drops it out of the roving-tabindex sweep.

`CpTabsComponent`'s tablist wraps onto more rows rather than scrolling sideways. SC 1.4.10 (Reflow) asks for no horizontal scrolling at 320 CSS px, and a row of tabs is not two-dimensional content that earns the exception, so a long list that cannot fit gets a second row where a scroller would have hidden tabs off the edge. Each tab also carries `min-height: 2.5rem`, because the label's own line box left the target around 32px — short of the 40px above.

A `cp-tabs` may be nested inside another's `cp-tab-panel`; the editor's Tools area does exactly that. Give each tablist its own `ariaLabel`, and keep tab ids unique across the two levels for the sake of the `panel-<id>`/`tab-<id>` element ids the panels are wired up with. Keyboard focus is safe either way: the outer tablist is a sibling of the projected panels rather than an ancestor, so an inner keypress never reaches the outer handler, and each instance's focus lookup is scoped to its own tablist rather than to its whole host subtree.

`CpFieldComponent` sets `box-sizing: border-box` on the control projected into it. The control is `width: 100%` and carries its own padding and a 1px border, so under the default `content-box` every field rendered about 28px wider than the column holding it. That stayed invisible for as long as forms sat in a roomy fixed-width column and surfaced as a horizontal scrollbar the moment one did not.

### The shape of a form

Every form in the app is built the same way, and `RecipeEditorComponent` is the reference because it is where
the shape was worked out. A form is a **card**, a **legend**, and **named sections** stacked `--cp-space-8`
apart. Nothing about that lives in a feature's stylesheet any more: a section is a
**`CpFormSectionComponent`**, and a line of short fields inside one is a **`CpFieldRowComponent`**.

A section carries four things and a consumer supplies them as inputs, never as markup: a `heading`, an
optional `intro` of one sentence, an optional `problem` — the server's word on the whole part when it refused
— and the fields projected into it. The section names itself from its own heading, and while a problem is
showing it is described by it, so a form that moves focus to a refused section reads the heading and then the
reason. `sectionId` gives the `<section>` an id for a form that scrolls or focuses it; it is already
`tabindex="-1"`, and never in the tab order.

**Fields stack, one per line, each capped at `--cp-measure-field`.** That is the default and it is most of
what a form is: a single column of controls at a readable width, with the rest of the card left empty beside
them. Empty space to the right of a field is the layout working, not a gap to fill — a control stretched to a
card's full width is harder to scan and no easier to fill in.

A `cp-field-row` is the exception, not the tool for tidying a long form. It is for values that are short *and*
read as one set: three durations, a quantity and its unit. Prose, lists and anything with a `textarea` stay
stacked, however many of them there are — `Description` and `Headnote` sit one above the other in the recipe
editor for that reason, and four comma-separated list fields belong in a column too. Widen `minColumn` when a
row's fields need more room than the 14rem floor.

A field projected straight into a section is capped at the measure; a field inside a *component* projected
there is that component's business and is left alone, which is why the measure rule stops at a direct child.

Optionality is stated once, in a legend above the sections, never field by field: marking one optional field
among a dozen unmarked ones makes the rest read as required. Which way round the legend runs depends on the
form — the recipe editor marks its three required fields, the AI brief says every field is optional.

A form inside a multi-step flow puts that legend in the *shell* rather than in each step, with its text on the
step's own definition: one implementation, and no step can forget it. `BrandSetupShellComponent` does this, and
it is why none of its steps marks a field optional.

### What the product says about the thing on screen

Saved, refused, shelved, still working: one sentence, in a `CpNoticeComponent`. The recipe editor worked the
shape out as a `.banner` family and every screen written after it either copied those rules or drifted from
them — by the time this became a component there were four private versions of it.

Tone never carries the meaning on its own. Each of `neutral | success | warning | error` brings a glyph as well
as a fill, because someone who cannot tell two hues apart still has to be able to tell "saved" from "could not
save"; a consumer may replace the glyph but not switch it off. Liveness is the consumer's: it sets
`role="status"` or `role="alert"`, since only the consumer knows whether the sentence interrupts. A live region
also has to be in the DOM *before* it has text to be announced reliably, which is what `quiet` is for — it
holds the place without a fill, padding or glyph. Content is projected, so an inline `Reload latest` sits in the
flow of the sentence it belongs to rather than under it.

Three utilities in `global.css` carry the rest of what features kept re-declaring: `.cp-muted` for supporting
copy, `.cp-actions` for a wrapping row of buttons under the thing they act on, and `.cp-disclosure` for a
`<details>` whose summary would otherwise sit under the 40px target.

### Fitting a form into the width it has

Two CSS traps cost real time here, both worth recognising rather than rediscovering.

A grid track declared `auto` — which is what every `display: grid` gets when no `grid-template-columns` is given — has a floor of its content's **max-content** width. So one wide descendant, or one child with a `max-width`, can hold an entire single-column grid wider than the container and push the page sideways. Single-column layout grids that must be able to shrink take `grid-template-columns: minmax(0, 1fr)`. For the same reason a reading measure belongs on a wrapper that is free to shrink, not on the control itself, where the `max-width` simply becomes the track's floor.

`minmax()`'s first argument is also a hard floor, so `repeat(auto-fit, minmax(26rem, 1fr))` keeps a single 26rem track in a container narrower than 26rem instead of shrinking. Where the pattern is genuinely "as many as fit", clamp the floor: `minmax(min(26rem, 100%), 1fr)`. Where the content is a fixed pair, say so with `repeat(2, minmax(0, 1fr))` under a container query — `auto-fit` on a fixed pair lays down a third track on a wide monitor that nothing will ever fill.

### Composing a control into `CpFieldComponent`

`CpFieldComponent` owns the label, hint and error of a field, and styles any `input`, `select` or `textarea`
below it. A control that is more than one element — a combobox with a popup, say — composes *inside* it rather
than replacing it: the consumer passes the field's `forId` to the control, which puts that id on the real input,
so the label labels the thing a screen reader lands on. `CpComboboxComponent` is the reference.

Three consequences worth stating. Such a control must not restyle the text input itself, or it will drift from
every other field in the product — the one sanctioned exception is making room for its own furniture, which is
why `CpComboboxComponent` reaches in for a single `padding-inline-end` and nothing else. It is therefore only
supported inside a field — used bare, its input is unstyled, which is a defect in the consumer rather than in
the control.

And because it inherits the field's ordinary input styling, it has to say that it is more than an input. A
combobox that looks exactly like a text box *is* a text box as far as anyone can tell: a creator clicks it,
nothing happens, and the picker is reported missing. So a composed control carries its own affordance — a
caret, and a pointer gesture that opens it — while leaving keyboard focus alone, since tabbing past a field is
not a request to open anything.

And because the control is projected, no template binding can reach it — so `CpFieldComponent` writes
`aria-describedby` (the hint, then the error) and `aria-required` onto it after each render. Without that the
`*` and the hint are decoration: three consumers had already worked around it by wiring `aria-describedby`
themselves. A consumer's own value is merged rather than replaced, since a control may be described by
something the field knows nothing about.

Marking optionality is the inverse: only required fields carry the `*`, and the convention is stated once per
form rather than on each field. Marking one optional field among a dozen unmarked ones makes the rest read as
required.

### Native form controls and `color-scheme`

`themes.css` sets `color-scheme` per theme, which is what makes native scrollbars and form controls follow the
theme instead of fighting it. The cost is that any control left unstyled is painted by the browser under that
scheme: an unstyled checkbox in dark mode renders its **unchecked** box as a dark filled square, which reads as
checked at a glance, and it arrives at 13×13 with no author focus ring — under both the 40px target and the
"never by colour alone" rule. A text input escapes this because `CpFieldComponent` already restyles it.

So a control this system offers is drawn, not left to the scheme. Drawing one means `appearance: none`, after
which every state — unchecked, checked, hover, focus, disabled — has to be authored anyway, which is why it
belongs in one library component rather than in each feature's stylesheet. `CpCheckboxComponent` is the
reference: the real input stays in the DOM, focusable and in the accessibility tree, and is only visually
replaced by a box beside it, so keyboard behaviour and what a screen reader announces stay the browser's. Its label is control-sized; a surface read from further away sets `--cp-checkbox-font-size` on the host to a type token.

`CpChoiceGroupComponent` is the same rule applied to a *set* of answers, and the shape to reach for whenever a
form asks the creator to choose rather than to type. Its options are **tiles**: the whole option is the target,
which is what keeps an option carrying a label, a line about what it means and a sample sentence scannable, and
what gets it past 40px without each feature inventing padding. A picked tile fills `--cp-primary-soft` behind an
evergreen border and answers the pointer with the interactive card's own lift, so choosing reads as the same
gesture as the rest of the product. It takes one answer or several, and a `max` disables the unpicked rest at
the cap rather than letting a pick disappear.

A single-answer group has no deselect — a radio cannot be unticked by clicking it — so a question that must be
answerable with *no answer* gives that its own option. The brand questionnaire's "Not sure yet" is one, and it
stands for the empty answer rather than being stored as a choice.

### Tabs, anchor navs, and one Save

Tabs switch between alternative views of a thing. The danger in using them for parts of *one* form that a single Save writes is that a panel nobody clicked can hide unsaved edits and validation errors, leaving a "there are unsaved changes" indicator able to say only that *something* changed, never where. That danger is answerable, and `RecipeEditorComponent` is the reference for how: its `Edit` area is split into General, Ingredients and Instructions tabs, and

- dirty state and the baseline snapshot are per **form**, never per panel, so the indicator lights for an edit in any tab and one Save writes all of them;
- a field error reveals itself — `revealFirstFieldError()` selects the tab holding the field before scrolling to and focusing it, which it must, because a panel renders its content only once its tab has been selected and keeps it merely `hidden` afterwards;
- panels stay mounted once opened, so no edit is discarded by looking somewhere else.

Without those three, prefer one scrolling surface. Tabs stay the right control for the genuinely separate journeys beside the form (Tools, Media, History), which read a *saved* recipe.

Inside a long surface, a **`CpAnchorNavComponent`** moves a reader between named sections. It is navigation, not a second tablist: every item is tabbable in order with no roving tabindex, and everything it names is present and submitted whether or not it is in view. Render it only when there is something to navigate — two or more sections — and never as an empty shell. The gutter it sits in belongs to the consumer: gate the two-column layout on the nav actually being rendered, or the content itself lands in the nav's track and every field inside it stacks into a narrow strip.
