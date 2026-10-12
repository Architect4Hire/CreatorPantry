# CreatorPantry visual system

## Direction

CreatorPantry is a focused production workspace for food bloggers and content creators—not a recipe index. The interface pairs the brand's warm, hand-in-the-kitchen energy with productivity-tool density: bold and rounded where the [logo](public/images/logo.png) is bold and rounded, vivid where a pantry of fresh produce is vivid, never flattened into a muted corporate palette. Fredoka gives content moments the same friendly, chunky display voice as the wordmark; DM Sans keeps controls and data highly legible. Evergreen (drawn from the mixing bowl in the mark) is the action color. Pink, orange, purple, and blue—each saturated enough to read as fresh tomato, carrot, eggplant, and blueberry rather than a desaturated tint—distinguish tools and states without competing with primary actions.

## Layers

1. Primitive tokens define scale: spacing, type, radii, motion, z-index (`--cp-z-topbar` < `--cp-z-sidebar` < `--cp-z-overlay` < `--cp-z-modal`), and the page frame (`--cp-page-gutter`; `--cp-page-width-reading`, `-standard`, `-wide`) — see "Composing a page" — and the drawer's two widths (`--cp-drawer-width`, `--cp-drawer-width-wide`).
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
- The page title is one size on every page: `--cp-font-size-title` (2rem), Fredoka at weight 400. It is the `h1` `CpPageHeadComponent` prints, and a card heading on the same page sits a step below it at `--cp-font-size-xl`.

## Composing a page

### The frame

Every signed-in page sits in one frame, and the app shell owns it. `AppShellComponent` wraps its router outlet in a single `.page-frame`, so every page — its title included — starts from the same left edge and none decides its own.

- **The gutter is the shell's.** The frame pads itself with `--cp-page-gutter` on both sides and at the bottom: `--cp-space-8`, dropping to `--cp-space-4` at `40rem` and narrower. A routed page does not set `padding` on its `:host`. One that does puts a second gutter inside the first.
- **The width is the shell's, in three steps.** `--cp-page-width-reading` (`52rem`) for a page that is one column of prose or one short form; `--cp-page-width-standard` (`80rem`), which is what a page gets by saying nothing; `--cp-page-width-wide` (`90rem`) for a page that lays tools or columns side by side — the recipe editor, the kitchen view, the concept studio and Image Studio. The widths include the gutter. A routed page does not set `max-width` on its `:host`; a measure inside the page (`--cp-measure-field`, `--cp-measure-prose`) is still the page's own.
- **A route picks its width beside its title.** `data: { title: 'Edit recipe', pageWidth: 'wide' }`. The shell reads `pageWidth` from the deepest matched route that names one and ignores a value that is not one of the three steps. There is no fourth step and no per-page number: a page that seems to need one needs a different layout inside the frame.
- **The frame is left-aligned.** A page keeps its edge against the sidebar at every window width; it is not centred in whatever is left over.
- **A page may still be a container.** `container:` on a `:host` is the page measuring the space the frame gave it, and it stays. The editor, the kitchen view and the concept studio size themselves that way.
- **On paper the frame is nothing.** Under `@media print` it gives up its padding and its width, as `<main>` gives up its own offsets, so a feature's print rules lay out the content and nothing else.

`scripts/check-page-contract.mjs` runs first in `npm test`. Its `frame` rule follows every route the shell mounts to its component's stylesheet and fails when a `:host` rule sets `padding`, `margin`, `width` or `max-width`. Sign-in, sign-up, confirm-email and the landing page sit outside the shell and are not in the frame. The rest of what it holds a page to is under "What the build checks".

### The page head

A page has one `h1`, and `CpPageHeadComponent` prints it; the shell prints none. From the top: the breadcrumb, the title, badges, a lede of a sentence or two, and the one primary action. Any of the four around the title may be absent, and an absent one takes no room.

- **The breadcrumb is one link, to the page this one sits under**, drawn with `cpBackLink` on the page's own `<a>` in the head's breadcrumb slot. The rule, which replaced twelve wordings:
  - A page has a breadcrumb only if it sits under another page. Brand settings, Workspace settings and the AI allowance page sit under nothing and have none.
  - It is one link, to the parent in the route tree — not to wherever the creator happened to come from, which a link cannot know.
  - Its text is the parent's route `title`, exactly: "DAM", "Prompt Library", "Brand library", "Brand settings", "Guide history", "Workspace settings". Never "Back to…", and no typed arrow: `cpBackLink` draws one and hides it from a screen reader.
  - It is there in every state. A page that could not find its record still knows what it sits under, so not-found and failed-to-load keep the link and need no "Back to the library" button of their own.
  - Where the page it sits under is itself the record that could not be found — the kitchen view of a recipe that is gone — the link goes one further up, to the nearest page that exists.
- **The title is the route's name, or the record's.** A page about one record prints that record's own title; every other page prints its route's `title` through `injectRouteTitle()`, so the tab and the page cannot disagree. A page whose state changes what it is — not found, removed — changes the head's `heading` rather than printing a second heading somewhere else.
- **Focus goes to the title when the page replaces what was being acted on.** `focusHeading()`; the ring shows on `:focus`, because script focus after a pointer click is not keyboard focus to a browser.
- **A page head carries `--cp-space-6` beneath it.** A page laid out as a grid, whose gap is already that space, zeroes the margin.

### A grid of cards

A library is a list, and `CpCardGridComponent` lays that list out: `cpCardGrid` on the page's own `<ul>`, one `<li>` per card. It fits as many columns as the frame allows and drops to one at a narrow width or high zoom, because the floor is `min(16rem, 100%)` and never wider than its container. It fills rather than fits, so a library with two things in it shows two card-sized cards. Raise `minColumn` for cards that need more room to be read; do not set a column count.

### A card for one thing in a library

`CpMediaCardComponent` is one thing shown by its picture. The picture is the feature's own component, projected, because the feature authorises and loads it; where there is none a tinted panel holds the same 4:3 shape with a glyph, so a row of cards stays even. The title is the link and the card is not: one link named by the title is what a screen reader should find. Facts go inline under the badges, and an action that does not open the thing goes last.

What goes in each slot is the same on every library, so a creator who has read one card can read them all:

- **Picture** — the thing itself, when the list response carries one and the feature can load it by id. Where there is none the card takes the panel, with a glyph for the kind of thing: `▣` a picture, `⌘` a recipe, `❝` a prompt, `◆` a brand example. Use the glyph the library's first-use empty state uses. A card never borrows a picture from something related to stand in for its own.
- **Title** — the thing's own name, as the link to its page. Nothing else on the card is a link to that page.
- **Description** — the creator's own words about it, or the start of it: a recipe's description, the opening of a prompt. One run of plain text; line breaks are kept where the whole thing is shown, not here. End it with an ellipsis only when the text really is cut. A thing with no words of its own has no description, not a placeholder.
- **Badges** — what state it is in, then what kind of thing it is. State is a `cp-status-pill`, because a state has a tone and a glyph (Draft, Approved, "Ingredients need review", "Text ready"); kind is a `cp-badge` (channel, document type, source). State comes first. A fact with a number or a date in it is not a badge.
- **Facts** — what is measured or dated: file and size, version, tags, when it was updated. `dl[cpFacts]` with `layout="inline"`, a handful at most, and a fact with nothing to say is left out rather than shown empty. Tags are one fact, comma-separated.
- **Actions** — `[cpMediaCardActions]`, for the rare thing that can be done from the list without opening the card: the Prompt Library's Preview. Small, secondary, and named for its card (`aria-label="Preview Chili hero"`) since every card has one. Editing, deleting and anything that needs the whole record belong on the thing's own page.

### A card for one topic on a detail page

`CpTopicCardComponent` is a `cp-card` with a heading that names its region, a body held to `--cp-measure-prose`, and its actions underneath. The heading can take focus by script and is never a tab stop (`focusHeading()`), for a card that has just replaced the one the creator was working in. A card stretches to the frame; a paragraph that did too would be unreadable, so the measure is the default and `wide` is the exception, for a table or a preview. Its heading is a step below the page title, at `--cp-font-size-xl`.

### Facts

Term-and-value pairs are a `<dl>` with `cpFacts`, one `<div cpFact label="…">` per pair. `stacked` on a page about the thing, where a value may run to a paragraph; `inline` on a card, where there are a few short ones. A value that is the creator's own words takes `prose` and keeps its line breaks.

### Search and filters in a toolbar

A library's toolbar holds `cp-search-field` and `cp-select-field`, each around the page's own native control. Their labels are announced and not shown: the placeholder, the first option and the toolbar's place on the page already say what each is, and four visible labels above a row of four small controls is noise. That is a property of a toolbar, not a licence — a field in a form still shows its label through `CpFieldComponent`.

### The steps of a flow

`CpStepperComponent` lists a flow's steps in order and says where the creator is. Each row is the step's name with its state under it in a word — "Not started", "You are here", "Done", "Skipped" — beside a glyph (○ ● ✓ ↷), and the current row is bordered, filled and carries `aria-current="step"`. The word is what a screen reader hears; the glyph is hidden from it.

It is a read-out. A row is not a link and takes no click: a flow moves by its own Back and Continue, which know what has to be saved and what the step still needs, and a stepper that let the creator jump would have to know that too. Use it for a flow whose steps are taken in order. Do not use it as tabs, as page navigation, or as a progress bar — `cp-progress` is the bar, and the two sit together. The consumer owns the `<nav>` it sits in and whether it folds away when the page is narrow.

### A chip or a select

A filter in a toolbar is one of two controls, and what decides is how many answers it has and where they come from.

**A chip** (`button[cpChip]` in a `[cpChipGroup]`) is for a filter with a few fixed answers that the creator should see all at once: a recipe's status, "Mine", Active or Archived. Every answer is on screen, the ones in force are visibly on, and changing one is a single press. A chip says it is on with `aria-pressed`, a border, a fill and a heavier label, so the state is never colour alone. The group is named by its `label`, which is announced and not shown, for the same reason a toolbar field's is. Whether one chip may be on or several is the page's rule: the chip takes `pressed` and leaves `(click)` to the page.

**A select** (`cp-select-field`) is for everything else: a filter with more answers than fit in a row, answers read from the server (channels), a filter where "any" is the usual state, and every sort. A row of twelve chips is a wall, and a chip for a channel that has not loaded yet is a control that appears late.

Chips are not tabs, not a choice in a form (`CpChoiceGroupComponent`) and not a badge that can be clicked. A chip never navigates and never removes itself.

### The two shapes: a library and a detail page

Most pages are one of two things, and DAM is the reference for both. Build a new one from the same parts in the same order rather than from a feature stylesheet.

**A library** lists many things of one kind (`DamLibraryComponent`):

1. `cp-page-head` — the library's name, a lede saying what is kept here, and the one primary action (add, upload, new). It is in the head and not beside the list, so a list that failed to load never takes it away.
2. `cp-toolbar` — `cp-search-field` in the search slot; in the filters slot a `[cpChipGroup]` for a filter with a few fixed answers and a `cp-select-field` for each of the rest ("A chip or a select"); the sort and "Clear filters" in the actions slot.
3. A `cp-notice` under the toolbar when a filter's own data could not be loaded, so the list is still shown.
4. `cp-list-shell` — it owns loading, error and empty; `cp-empty-state` distinguishes "nothing here yet" from "nothing matches these filters".
5. `ul[cpCardGrid]` of `cp-media-card` — the picture projected by the feature or the panel in its place, the title as the link, state and kind in the badges slot, and `dl[cpFacts]` with `layout="inline"` ("A card for one thing in a library" says what goes in each slot). Raise `minColumn` where the card's main content is text, as the Prompt Library does.
6. The pager in the list shell's pagination slot: a result summary in a polite live region and "Load more", which moves focus to the first new card's title. Pages are appended, never replaced: there is no Previous, Next or page number. A later page that fails keeps the cards and says so in a `cp-notice` beside the button; a first page that fails after cards were on screen may keep them too, with a notice that they are from a moment ago and no "Load more" until the search in force has answered.

The four libraries are the references: `DamLibraryComponent` for cards with pictures, `RecipeLibraryComponent` for chips, `BrandLibraryComponent` for chips beside selects and the kept-cards notice, and `PromptLibraryComponent` for a card action that opens a drawer. A library opens a card's own page from its title; a preview beside the grid is the exception, and where there is one it is a drawer (`PromptPreviewComponent`), never a dialog, so the grid, its filters and the creator's place in it stay in view.

**A detail page** is about one thing (`DamAssetDetailComponent`):

1. `cp-page-head` — a `cpBackLink` breadcrumb to the page it sits under, the thing's own name as the title, and its badges: state as `cp-status-pill` first, then kind as `cp-badge`, the order a card in the library gives them. One head for every state: not found and removed change the heading, they do not add a second one.
2. One fixed place for messages, directly under the head: a `cp-notice` that is always in the DOM (`quiet` until it has something to say) for what the page just did, then any problem, then what this person may not change here. A message about one card's own content — a list that could not load, a note on who may use an action — stays in that card.
3. A column of `cp-topic-card`, one per topic, each with its actions underneath: what it is, about it (`dl[cpFacts]`, stacked), where it came from, then anything tabular with `wide`. Every topic is on the page at once. Tabs are not used to file a record's topics: the Brand example page had four and now has four cards, and what was "switch to the Text tab" brings the text card into view.
4. An action sits at the bottom of the card it acts on, not in the head: "Replace file" under Versions, "Download" under what is being downloaded. The head's action slot is for a way out of the page, as Brand settings uses it.
5. The action that takes the thing away — remove, put on the shelf — is last, in a topic card of its own that says what it does before the button.
6. Editing the record happens in a drawer opened from the card it edits, and focus returns to that button ("Inline, a drawer or a dialog").

`DamAssetDetailComponent`, `PromptDetailComponent` and `BrandSourceDetailComponent` are the references.

**A settings page** is a detail page whose main topic is a form. It takes the same head and the same fixed place for messages, and then:

- **The form is one card, and stays one.** A form that one Save writes is a `cp-card` holding the legend, its `CpFormSectionComponent` sections and its Save, exactly as "The shape of a form" describes. It is not split into a topic card per section: a topic card names a region the creator reads, a form section names a part of one thing they submit, and a Save that sat under six cards would not say which of them it writes.
- **A topic card does not replace a form section.** A settings form's card is a plain `cp-card`: giving it a topic card's heading as well as its sections' would name one thing twice. The one place a topic card holds form sections is a step of a flow, where the card is named for the step and the sections are the parts of it ("The third shape: a flow").
- **Everything around the form is a topic card:** the first-use introduction above it, the link to a sibling page below it (Workspace settings' "Themed days").
- **A form's own save state stays beside its Save.** "Saved", "Unsaved changes" and a refused save are about the form, so they are announced where the creator's hands are. The fixed place under the head is for what is true of the whole page — it could not be loaded, this person may only read it, someone else changed it meanwhile.
- **The guards do not move.** A page with a `canDeactivate` guard or kept-through-errors edits has exactly the guard it had; the shape changes what is drawn, not when the creator is asked.

`BrandSettingsComponent`, `BrandGuideEditorComponent`, `WorkspaceSettingsComponent` and `ThemedDaysComponent` are the references.

### The third shape: a flow

A flow takes the creator through a piece of work in order, keeping what they enter as they go (`ContentPipelineShellComponent`, `BrandSetupShellComponent`):

1. `cp-page-head` — the flow's name, a lede saying what it makes and that it can be left and picked up, and the breadcrumb to the page it sits under. No action: a flow's actions belong to its steps.
2. A polite live region that is always in the DOM, for what a step change or a save just did.
3. Beside each other where there is room, stacked where there is not: the progress, and the step.
   - **The progress** is the flow's own `cp-progress` over a `cp-stepper` in the flow's own `<nav>`. The stepper says where the creator is; it is not how they move. A flow may fold the stepper into a disclosure at a narrow width, as Brand setup does.
   - **The step** is one `cp-topic-card`, `wide`, headed by the step's name. It holds the help disclosure, the legend, the step's `CpFormSectionComponent` sections, the save status, and Back / Continue last. When the step changes, focus goes to the card's heading (`headingId`, or `focusHeading()`), so the new step is announced and reading starts at its top.
4. **The legend is the shell's.** One sentence per step about what is optional, printed by the shell above the step's sections from the step's own definition, so no step can forget it and none marks a field optional.
5. **What stands in the way is said above the buttons it is about** — "Pick an idea to carry on" — not after them.
6. Before the first step and after the last, a topic card of its own: "Pick up where you left off" with Continue and Start over; "Setup finished." with the stepper as a summary. Each takes focus on its heading when it replaces the step.
7. A flow keeps its steps, their order, what is saved as the creator goes and how it resumes. The shape changes what is drawn, never when something is written.

A page that is none of the three — the recipe editor, the kitchen view, a studio that is one long form — still takes the frame and the page head, and owes nothing more to these shapes:

- **The recipe editor** is one form under one Save, so its head carries what acts on the whole recipe: the breadcrumb to Recipes, the recipe's name, and in the action slot the "Unsaved changes" and "Total time will be recalculated" notes, Print, Kitchen view, Archive and Save. Everything under the head is the form, and "Tabs, anchor navs, and one Save" governs it.
- **A studio that is one form** (Image Studio, the recipe concept studio's brief) keeps that form in one `cp-card` with its legend and sections, as "The shape of a form" has it. Its results — a chosen concept, what the model flagged — are topic cards under the list they belong to.
- **The kitchen view** is a page to cook from and to print. Its head is the recipe's name and description; the way back and the controls are for the screen only. On paper the head is the title and the description at the sizes the page has always printed them, which is the one place a feature stylesheet reaches into `cp-page-head` (under `@media print`, with `::ng-deep`), because a printed recipe sets its own type.
- **A long draft to review** (the first-draft review) keeps its own sections with a sticky anchor nav: each section's heading is a jump target that has to clear the nav, and each holds cards of its own, so the sections are not topic cards.

### Inline, a drawer or a dialog

Three ways to change something on a detail or settings page, and which one is not a matter of taste.

- **Inline** when the page is the form. Fields the page's own Save writes are on the page, in its form card, and never leave it for a drawer or a dialog. An inline form stays inline.
- **A drawer** when the creator is changing or adding to *the record on screen* and is better off seeing it while they do: editing a picture's details, logging a use of it, adding a version, replacing an example's file, correcting its text. The drawer has its own Save in its footer and writes on its own.
- **A dialog** when the task does not need the page: a short question with a field or two ("Write with version 3?" and why), picking one thing from a list (a logo), or creating something that does not exist yet (upload a picture, add an example). A yes-or-no with nothing to type is `ConfirmService`, not a dialog.

What a form owes the drawer it sits in:

- **It confirms before it discards.** `closeRequested` and the footer's Cancel go to one handler, which asks through `ConfirmService` when something has been typed and closes at once when nothing has.
- **Its opener is focused before it opens.** The drawer gives focus back to whatever held it at open, and a click does not focus a button in every browser, so the handler that opens the drawer focuses the button first.
- **It finds its own fields from the document.** An open drawer moves its content to `<body>`, so the form's host element no longer contains its inputs: focus an invalid field with `ownerDocument.getElementById(...)`, not `host.querySelector(...)`. A spec finds them from `document.body` for the same reason.
- **Escape that a control inside has used is marked handled.** The drawer ignores an Escape whose default was prevented, so a picker inside the form that keeps Escape for itself calls `preventDefault()` on it.

### A drawer, a dialog or a page

Three surfaces can hold a task, and which one is decided by what the creator needs to see while doing it.

**A page** is where the task is the destination. Use it when the creator would link to it, come back to it or leave it half done; when it is more than one screen of fields or more than one step; or when it needs the full width. A form the page's own Save writes is part of that page and never leaves it.

**A drawer** (`CpDrawerComponent`) holds one task the creator does *about the page while still reading it*: a small edit to the record on screen, a preview of something in a library, a tool run beside the thing it works on. It has its own actions in its footer, and they finish the task. `standard` (`--cp-drawer-width`, `30rem`) for a short form; `wide` (`--cp-drawer-width-wide`, `48rem`) for a comparison or a preview. A drawer never holds a form the page's Save writes, a confirmation, navigation or a multi-step flow, and it is not a way to tidy a busy page. It may open on a choice of *which* task — the recipe editor's Tools drawer lists its seven tools first — because choosing the task is not a second task; what it may not do is lead from one finished task into another. Nothing opens on top of a drawer except a confirmation.

**A dialog** (`CpDialogComponent`) hosts one short thing that does not need the page in view: creating something new from a few fields, or picking one thing from a list. It interrupts, so it is brief.

**A lightbox** (`CpLightboxComponent`) shows one picture larger than the page has room for, and nothing else: no form, no task, no second picture beside it. The picture is the consumer's own `<img>`, held to the window's height and to its own pixels, so it is never scaled past the size it was made at. What is known about the picture goes in its caption, and a consumer with a set of pictures puts Previous and Next in its footer and moves between them itself. The control that opens it is a button beside the picture reading "View larger", not the picture itself: a picture can hold a control of its own, and its alt text is not the name of an action. `QuickRecipeComponent` is the reference for one picture, `ContentPipelineImageLightboxComponent` for a set.

**A confirmation** is none of these. It is `ConfirmService`; see "Confirmations".

When two fit, ask in order: does the creator need to see the page to do this? Then a drawer. Would they want its address? Then a page. Otherwise a dialog.

What a drawer does, so a feature does not have to:

- **It asks to close; it does not close.** Escape, the scrim and the close control each emit `closeRequested` with which one it was, and the drawer stays open until the consumer sets `open` to false. A drawer holding unsaved edits answers through `ConfirmService` and closes only on "Discard"; one holding nothing closes at once. A Cancel button in the footer goes through the same handler.
- **The page behind is inert, not merely covered.** While open, the drawer moves its scrim and panel to `<body>` and makes everything else there `inert`, so nothing underneath can be clicked, tabbed to or read out. That move is also what puts it above the sidebar and the top bar: the scrim is on `--cp-z-overlay` and the panel on `--cp-z-modal`, and it adds no layer of its own. A spec finds an open drawer from `document.body`, not from its fixture.
- **A toast raised while a drawer is open is not announced,** because the toast region is part of what is inert. What a drawer's own task has to say — saved, refused — goes in a `cp-notice` inside the drawer.
- **Focus goes in, stays in, and comes back.** It lands on the panel, so the heading and description are read, or on an element in the body carrying `autofocus`. Tab wraps inside. If focus is lost to the page body while the drawer is open — a task inside removed or disabled the control that held it — Escape still asks to close and Tab comes back to the panel, unless a confirmation is raised over the drawer, whose keys are its own. On close it returns to the control that opened the drawer — including when the close came from "Discard" in a confirmation, which is holding focus at that moment. A page that wants focus somewhere else moves it after the drawer has closed.
- **Only the body scrolls.** The header and footer stay put, the page behind is locked, and the `<body>` styles it changed to do that are put back exactly as they were.
- **At `40rem` and narrower it is a full-screen sheet,** for both widths. That is the breakpoint `--cp-page-gutter` drops at, and where a 1280px window lands at 200% zoom. A media query cannot read a token, so the number is written in the component and here.
- **It slides on `--cp-duration-normal` and `--cp-ease`,** and does not move at all under `prefers-reduced-motion`.

`CpDialogComponent` and `CpLightboxComponent` share the focus return, the Tab trap and the scroll lock (`modal-surface.ts`, internal to the library). Each stays where it is in the DOM and does not make the page inert. They are two components: a drawer is not a dialog variant, and neither takes an input that turns it into the other.

### What the build checks

Before anything else about a new page, say which of the three shapes it is — a library, a detail page or a flow. A page that is none of them is a question to raise, not a fourth shape to draw.

`scripts/check-page-contract.mjs` runs first in `npm test` and fails the run when a page drifts from this section. Karma runs in a browser and cannot read a stylesheet from disk, which is why it is a script and not a spec; `scripts/check-page-contract.test.mjs` runs before it and proves each rule still fires. Two rules judge routed pages only, and the rest judge every template and stylesheet under `src/app` inside the shell, because a part redrawn in a child component is still redrawn.

| Rule | Fails when | Use instead |
| --- | --- | --- |
| `frame` | a routed page's `:host` sets `padding`, `margin`, `width`, `max-width` or their logical forms | the shell's frame, and `data: { pageWidth }` on the route |
| `one-h1` | any template renders an `<h1>` | `cp-page-head`, which prints the one `h1` |
| `page-head` | a routed page has no `cp-page-head` or renders a `<header>`; a stylesheet styles `.page-head`, `.page-header`, `.page-title`, `.back` or `.breadcrumb` | `cp-page-head` and `a[cpBackLink]` |
| `card-grid` | a stylesheet styles `.grid`, `.card-grid` or `.cards`, or lays out a `repeat(auto-fill, …)` or `repeat(auto-fit, …)` grid | `ul[cpCardGrid]`; `cp-field-row` for short fields; `dl[cpFacts]` for terms and values |
| `facts` | a stylesheet styles `.facts` or `.fact` | `dl[cpFacts]` of `div[cpFact]` |
| `toolbar-field` | a stylesheet styles `.search-field`, `.select-field` or `input[type=search]`; a search input sits outside both `cp-search-field` and `cp-field` | `cp-search-field` and `cp-select-field` in a toolbar; `cp-field` where the search has a visible label |
| `stepper` | a stylesheet styles `.step-list`, `.steps` or `.stepper` | `cp-stepper` |
| `chip` | a stylesheet styles `.chip` or `.chips` | `button[cpChip]` in a `[cpChipGroup]` |
| `drawer` | a feature stylesheet sets `position: fixed` or slides with `translateX` | `cp-drawer`, `cp-dialog`, or `cp-lightbox` for a picture |
| `utilities` | a stylesheet styles `.cp-muted`, `.cp-actions` or `.cp-disclosure`, or keeps a private `.muted` or `.actions` | the class from `global.css`, with a second class of the feature's own for anything more it needs — a measure, a larger size |

The class-name rules are by name on purpose: the names are the ones 94 stylesheets used for these parts, and a private rule under one of them is how each was redrawn. A thing that merely shares a name — a line of file details called `.facts` — is renamed for what it is.

A rule is never loosened to let a file through. The file is fixed, or it is listed in `EXEMPTIONS` at the top of the script with the rule it is excused from and the reason, so granting one costs a line a reviewer reads; an exemption that no longer excuses anything fails the run too. The standing ones are the shell's own fixed chrome, the kitchen view's print and across-the-kitchen rules, the recipe's method steps (which are not a flow's), two pairs of panels set side by side, and the first-draft review's across-reading values, which goes when `dl[cpFacts]` has an across layout.

## Confirmations

A confirmation — a question with two answers, one of which loses work the creator cannot recover — is a SweetAlert2 modal raised by `ConfirmService` (`src/app/core/confirm.service.ts`), never hand-built markup and never `CpDialogComponent`. `CpDialogComponent` stays the right surface for a modal that *hosts* something, such as the workspace-creation form.

The popup is appended to `<body>`, outside Angular's style encapsulation, so it is themed in `src/styles.css` through the `cp-swal*` class names with `buttonsStyling: false`. Every value there is a `--cp-*` token, which is what makes a confirmation follow `data-cp-theme` into dark mode rather than arriving in SweetAlert2's own palette. Confirm wording names the action ("Discard changes"), never "OK"; the cancelling button holds focus; and dismissing by Escape, backdrop or close always means "stay".

A browser close, refresh or full navigation cannot be confirmed this way — the browser shows its own message — so that path remains a `beforeunload` listener.

## Accessibility baseline

Target WCAG 2.2 AA. Preserve visible focus. Do not communicate status by color alone. Touch targets should be at least 40px. Dialogs, drawers and lightboxes require a title and close action, hold Tab inside themselves, and return focus to what opened them. Form errors remain adjacent to their field and use `role="alert"`. `global.css` honors `prefers-reduced-motion: reduce` for every animation/transition; do not add motion that bypasses it.

## Meters and figures

A meter reads as an amount when the question is "how much have I got" and as a percentage when it is "how far through is this". `CpProgressComponent` does both: `valueText` replaces the percentage figure and is announced as `aria-valuetext`, so a screen reader hears the same words a sighted reader sees rather than a fraction nobody asked for.

Its `tone` colours the fill and does nothing else — it adds no glyph, changes no text and changes no ARIA. That is deliberate: a bar that only changed colour would be status by colour alone. Pair a toned meter with a `CpStatusPillComponent` naming the state in a word, and the meter's colour becomes the quieter half of something already said.

A figure is bold `--cp-font-sans`, not the display face: metrics are read, not declaimed. Put the reading measure on a wrapper free to shrink rather than on the track, where a `max-width` becomes the meter's floor instead of its ceiling.

Toolbar arrow-key navigation (`CpToolbarComponent`) yields the arrow, `Home` and `End` keys to a focused text input, `<select>`, `<textarea>` or contenteditable, so a control in its search slot keeps its own caret and value behavior.

Its "More actions" disclosure appears only when the consumer projects something into `[cpToolbarOverflow]`. A control that opens onto an empty popover is a dead end for every user and a mislabeled target for a screen reader, so an empty slot hides the disclosure entirely — which also drops it out of the roving-tabindex sweep.

`CpTabsComponent`'s tablist wraps onto more rows rather than scrolling sideways. SC 1.4.10 (Reflow) asks for no horizontal scrolling at 320 CSS px, and a row of tabs is not two-dimensional content that earns the exception, so a long list that cannot fit gets a second row where a scroller would have hidden tabs off the edge. Each tab also carries `min-height: 2.5rem`, because the label's own line box left the target around 32px — short of the 40px above.

A `cp-tabs` may be nested inside another's `cp-tab-panel`; the recipe editor's Edit area does exactly that, with the form's three tabs inside it. Give each tablist its own `ariaLabel`, and keep tab ids unique across the two levels for the sake of the `panel-<id>`/`tab-<id>` element ids the panels are wired up with. Keyboard focus is safe either way: the outer tablist is a sibling of the projected panels rather than an ancestor, so an inner keypress never reaches the outer handler, and each instance's focus lookup is scoped to its own tablist rather than to its whole host subtree.

`CpFieldComponent` sets `box-sizing: border-box` on the control projected into it. The control is `width: 100%` and carries its own padding and a 1px border, so under the default `content-box` every field rendered about 28px wider than the column holding it. That stayed invisible for as long as forms sat in a roomy fixed-width column and surfaced as a horizontal scrollbar the moment one did not.

### The shape of a form

Every form in the app is built the same way, and `RecipeEditorComponent` is the reference because it is where
the shape was worked out. (Its Save is in the page head rather than under the form, because the form runs
across tabs and the head is the one place every tab can reach.) A form is a **card**, a **legend**, and **named sections** stacked `--cp-space-8`
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
step's own definition: one implementation, and no step can forget it. `BrandSetupShellComponent` does this —
the legend is printed inside the step's topic card, above the step's sections — and it is why none of its
steps marks a field optional. `ContentPipelineShellComponent` does the same.

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
copy, `.cp-actions` for a wrapping row of buttons under the thing they act on (it takes no room while it has
no buttons in it), and `.cp-disclosure` for a `<details>` whose summary would otherwise sit under the 40px
target. A feature stylesheet neither restyles one nor keeps a private `.muted` or `.actions` beside it; the
build fails on either.

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

Without those three, prefer one scrolling surface. Tabs stay the right control for the genuinely separate journeys beside the form (Media, History, Test kitchen, Readiness, Publish), which read a *saved* recipe.

The recipe's tools are not one of those tabs. Scaling, converting, yield and display, and the four AI requests used to be a tab holding a second tab set of seven; they are now one **Tools** control in the page head that opens the wide drawer beside the recipe, so a scaled recipe or a proposed change is read next to the recipe it was made from and the editor has one level of tabs fewer. What holds for it:

- **It is locked by what locked the tab, and says so in the same words.** Before the first save there is no saved recipe for a tool to read. The control is `aria-disabled`, not `disabled`, so it can still be reached and is described by the line that explains the greyed-out areas, which still names Tools. Unsaved edits do not lock it: each tool says for itself what a dirty form means for it.
- **The drawer opens on the list of tools, then on the tool that was last open.** Choosing a tool puts its name in the drawer's heading and an "All tools" control above it, which takes focus, because the button that was pressed went with the list.
- **A tool is mounted the first time it is chosen and then stays alive, drawer shut or not,** exactly as a visited tab panel does. A request that is running goes on being watched and a proposal nobody has answered is still unanswered on reopening. So closing the drawer accepts nothing, discards nothing and abandons nothing, and asks nothing. While a revision or adaptation request has been sent and not yet answered, the page head says in words that Tools has a request to come back to; it does not claim to know whether that request is still being written, is ready or has failed, which only the tool knows. The two advisory tools return something to read and nothing to decide, so they are never counted.
- **The drawer adds no shortcut.** Applying a calculation still goes through the editor's one handshake — confirmation, the recipe's token, a new version — and an AI change is still a diff reviewed in the tool before anything is written. What an apply did is said inside the drawer as well as on the page, because the page behind an open drawer is inert.

Inside a long surface, a **`CpAnchorNavComponent`** moves a reader between named sections. It is navigation, not a second tablist: every item is tabbable in order with no roving tabindex, and everything it names is present and submitted whether or not it is in view. Render it only when there is something to navigate — two or more sections — and never as an empty shell. The gutter it sits in belongs to the consumer: gate the two-column layout on the nav actually being rendered, or the content itself lands in the nav's track and every field inside it stacks into a narrow strip.
