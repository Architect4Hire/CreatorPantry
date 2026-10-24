// Fails when a signed-in page drifts from the page contract.
//
// The app shell's page frame owns the one gutter and the one width every signed-in page has, `cp-page-head`
// owns the one `h1`, and `@creator-pantry/ui` owns the parts every page is built from (DESIGN-SYSTEM.md,
// "Composing a page"). A page that pads its own host, prints its own header, or redraws a grid of cards, a
// facts list, a toolbar field, a step list, a chip, a sliding panel or one of the global utilities is how
// 94 stylesheets each came to answer the same question alone. Karma runs in a browser and cannot read a
// stylesheet from disk, so this is a script `npm test` runs first rather than a spec.
//
// Two scopes. The `frame` and `page-head` rules judge routed pages: every `loadComponent` and `loadChildren`
// reachable from the two routes that mount the shell. Every other rule judges every template and stylesheet
// under `src/app`, because a part redrawn in a child component is still redrawn.
//
// The public pages — the landing page and the legal documents — are the third scope: outside the shell, each
// framing itself. They answer to every part rule the shell's pages do, and to three of their own in place of
// the shell's frame: one headline per page; one gutter and three widths, all tokens; and buttons from the
// library. Sign-in, sign-up, confirm-email and the showcase sit outside the shell and are not judged.
//
// A rule is never loosened to let a file through. Fix the file, or list it in EXEMPTIONS with the reason; an
// exemption that no longer matches anything fails too, so the list cannot outlive what it excuses.

import { existsSync, readdirSync, readFileSync } from 'node:fs';
import { dirname, join, relative, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const appDir = resolve(root, 'src/app');
const appRoutes = resolve(appDir, 'app.routes.ts');
const SHELL = './shell/app-shell.component';
const OUTSIDE_SHELL = /[\\/](sign-in|sign-up|confirm-email|landing|design-system-showcase)[\\/]/;
/** The public pages: outside the shell, and judged by their own shape rather than skipped. */
const PUBLIC_PAGE = /[\\/]features[\\/]landing[\\/]/;
/** The template that holds each public page's one `h1`: the landing hero, and the legal document. */
const HEADLINE = /[\\/](?:landing-hero|legal-page)\.component\.html$/;
/** The hero is the one part of a public page that draws itself: its headline, its collage and their sizes. */
const HERO = /[\\/]landing-hero(?:-collage)?\.component\.(?:css|html|ts)$/;

/** What each rule protects, printed once under its failures. */
export const RULES = {
  frame:
    "The shell's page frame owns the gutter and the width. Remove the declaration; a page that needs another width asks with `data: { pageWidth: 'reading' | 'wide' }` on its route.",
  'one-h1':
    '`cp-page-head` prints the one `h1` on a page. Give the page a `cp-page-head` and start everything else at `h2`.',
  'page-head':
    'A routed page opens with `cp-page-head` (and `a[cpBackLink]` for its breadcrumb); it draws no header of its own.',
  'card-grid':
    'A list of cards is `ul[cpCardGrid]`; short fields in a line are `cp-field-row`; term-and-value pairs are `dl[cpFacts]`.',
  facts: 'Term-and-value pairs are `dl[cpFacts]` of `div[cpFact]`, `stacked` or `inline`.',
  'toolbar-field':
    'A toolbar\'s search box is the feature\'s own `<input type="search">` inside `cp-search-field` (inside `cp-field` where it has a visible label); a toolbar filter or sort is a `<select>` inside `cp-select-field`.',
  board:
    'Columns of ordered things are `cp-board-column` inside `[cpBoard]`; the feature supplies the list in each and how a thing moves.',
  stepper: 'The steps of a flow are `cp-stepper`.',
  chip: 'A filter with a few fixed answers is `button[cpChip]` in a `[cpChipGroup]`.',
  'stat-tile': 'A figure read at a glance is `cp-stat-tile`: the count and what it counts as one sentence, a line of context and a link (DESIGN-SYSTEM.md, "Meters and figures").',
  'option-card': "One choice among several, compared side by side, is `cp-option-card` in a `ul[cpCardGrid]`: its key facts as tags, the rest behind its disclosure, and its one button. A value a Save submits is `cp-choice-group` (DESIGN-SYSTEM.md, \"Choosing between options\").",
  drawer: 'A panel over the page is `cp-drawer` (or `cp-dialog`), and a picture shown large is `cp-lightbox`; a feature positions nothing against the viewport.',
  illustration: "A drawing for an empty state or a card with no picture is `cp-illustration` (or `cp-empty-state`'s `illustration`), one of the drawn set, and the panel a card without a picture takes is `cp-media-card`'s (`illustration` or `glyph`); a feature draws neither of its own.",
  generated: "What a model made is marked by `cp-generated-label` (or `cp-generated` around it), whose `kind` picks the words — \"Drafted by AI\", \"Made by AI\", \"Read by AI\"; a feature writes no label of its own (DESIGN-SYSTEM.md, \"Generated content\").",
  skeleton: "Loading draws `cp-skeleton` (`line`, `block`, `circle`, `card`), or `cp-list-shell`'s `loadingLayout`; a feature draws no placeholder shimmer of its own.",
  loading: "Something on its way is drawn as the shape that is coming (`cp-skeleton`, or `cp-list-shell`'s `loadingLayout`) and said once, in a visually hidden status that is always present (`.cp-sr-only`, role=\"status\"). No visible \"Loading…\", \"Reading your…\" or \"Checking…\" sentence stands in for it. A pager button may say \"Loading more…\"; a button reporting what the creator just pressed keeps its own words (APP-003, AU.7).",
  enter: "Content arriving fades and rises once, by the library's `cpEnter` — a list shell's ready content, a generated result, a group of options (DESIGN-SYSTEM.md, \"Content arrives once, gently\"). The entrance tokens, the `cp-enter` keyframes and `data-cp-enter` are the directive's: carry `[cpEnter]` rather than rebuilding them. Motion about something else — a tab panel's switch, the public hero's collage — is not this rule's.",
  tone: "A tool's tone is set by its route, `data: { tone }`, on the shell's page frame. A feature reads `--cp-tone`, `--cp-tone-soft`, `--cp-tone-text` and `--cp-tone-on`; it never sets `data-cp-tone` or declares one of them (DESIGN-SYSTEM.md, \"Warmth inside the app\").",
  utilities: '`.cp-muted`, `.cp-actions` and `.cp-disclosure` live in global.css. Use the class; do not redeclare it or keep a private `.muted` or `.actions` beside it.',
  'public-h1': "A public page has one `h1`: the landing hero's, or the legal document's title. Every section starts at `h2`.",
  'public-frame': "A public page frames each section from the public tokens: width `--cp-public-width`, `-narrow` or `-wide` (or `--cp-measure-prose`), gutter `--cp-public-gutter`. A section's `:host` sets no padding, margin or width, and a measure inside a section is a token, never a literal length. Only the hero sizes its own parts.",
  'public-control': "A button on a public page is `cpButton` (`<button cpButton>` or `<a cpButton>`), so its size, focus and states are the library's.",
};

/**
 * Every file excused from a rule, and why. `file` is relative to `src/web`, with forward slashes.
 * Granting one costs a line here that a reviewer reads.
 */
export const EXEMPTIONS = [
  {
    file: 'src/app/shell/app-shell.component.html',
    rule: 'tone',
    reason:
      "The page frame is where a route's tone is set, from its `data: { tone }`, and the one place it may be (AU.2.1).",
  },
  {
    file: 'src/app/shell/app-shell.component.css',
    rule: 'drawer',
    reason:
      "The sidebar and the top bar are the shell's own chrome: fixed to the viewport, and the sidebar slides in at a narrow width. Nothing else may be.",
  },
  {
    file: 'src/app/features/recipes/recipe-kitchen-view.component.scss',
    rule: 'facts',
    reason:
      'The one page that prints. Its times and yield are set large to be read from across a kitchen and reset in points for paper, and UI.6 held it to printing exactly as it did.',
  },
  {
    file: 'src/app/features/recipes/recipe-kitchen-view.component.scss',
    rule: 'stepper',
    reason: "`.step-list` is the recipe's method — the steps a cook follows — not progress through a flow.",
  },
  {
    file: 'src/app/features/recipes/recipe-kitchen-view.component.scss',
    rule: 'page-head',
    reason:
      "Under @media print only: takes the page head's breadcrumb slot off the paper. cp-page-head has no print behaviour of its own.",
  },
  {
    file: 'src/app/features/recipes/recipe-kitchen-view.component.scss',
    rule: 'utilities',
    reason:
      "Under @media print only: takes the page head's actions slot off the paper, in the same rule as the breadcrumb.",
  },
  {
    file: 'src/app/features/recipes/recipe-instructions-section.component.scss',
    rule: 'stepper',
    reason: "`.steps` is the list of the recipe's instruction steps being edited, not progress through a flow.",
  },
  {
    file: 'src/app/features/ai-usage/ai-usage.component.scss',
    rule: 'card-grid',
    reason: '`.breakdowns` sets two `cp-list-shell` panels side by side, not a list of cards.',
  },
  {
    file: 'src/app/features/recipes/recipe-duplicate.component.scss',
    rule: 'card-grid',
    reason:
      '`.summary` sets two short lists side by side — what a copy keeps and what it leaves — not a list of cards.',
  },
  {
    file: 'src/app/features/ai/recipe-first-draft-review.component.scss',
    rule: 'card-grid',
    reason: "`.draft-fields-grid` holds a draft's yield and timing, each value with an editor that opens in place. Settled in AU.5.1: `dl[cpFacts] layout=\"across\"` sizes each fact to its content and wraps, so an editor opening in one would reflow its neighbours under the creator's hand, and its bold value would bolden \"Not suggested\" and the Edit control. The draft's provenance, which has no control, moved to `across`.",
  },
  {
    file: 'src/app/shared/channel-posts/channel-post-picture.component.html',
    rule: 'loading',
    reason: "\"Reading the picture…\" is an AI reading of the picture that the creator has just asked for and is spending allowance on, not something already theirs on its way. It reports an action, as \"Asking for your posts…\" does (AU.7).",
  },
  {
    file: 'src/app/features/content-pipeline/content-pipeline-idea-step.component.html',
    rule: 'loading',
    reason: "\"Reading the name you typed…\" is an AI reading of the dish name the creator has just asked for, not something already theirs on its way. It reports an action (AU.7).",
  },
];

const FRAME_PROPERTIES =
  /(?:^|[;{\s])((?:padding|margin)(?:-[a-z-]+)?|max-width|width|inline-size|max-inline-size)\s*:/g;

/** Class names a feature stylesheet may not style, by the rule that owns them. */
const OWNED_CLASSES = {
  'page-head': [
    'page-head',
    'page-header',
    'page-title',
    'back',
    'breadcrumb',
    'page-icon',
    'head-icon',
    'title-icon',
    'head-media',
    'page-media',
    'title-media',
    'masthead',
    'title-row',
    'head-picture',
    'hero-image',
  ],
  'card-grid': ['grid', 'card-grid', 'cards'],
  facts: ['facts', 'fact'],
  'toolbar-field': ['search-field', 'select-field'],
  board: ['board', 'board-column', 'lane', 'lanes', 'kanban'],
  stepper: ['step-list', 'steps', 'stepper'],
  chip: ['chip', 'chips'],
  'stat-tile': ['stat', 'stats', 'stat-tile', 'stat-tiles', 'kpi', 'kpis', 'figure-tile', 'figure-tiles'],
  'option-card': ['option-card', 'option-cards', 'concept-card', 'concept-title', 'concept-dietary', 'variant-card'],
  illustration: [
    'illustration',
    'illustrations',
    'empty-art',
    'empty-illustration',
    'spot-art',
    'no-picture',
    'picture-panel',
    'picture-placeholder',
  ],
  utilities: ['cp-muted', 'cp-actions', 'cp-disclosure', 'muted', 'actions'],
};

const lineAt = (text, index) => text.slice(0, index).split('\n').length;
const blank = (text) => text.replace(/[^\n]/g, ' ');

/** Where the string opened at `at` ends, just past its closing quote (or the line, if it never closes). */
const stringEnd = (text, at) => {
  let end = at + 1;
  while (end < text.length && text[end] !== text[at] && text[end] !== '\n') end += text[end] === '\\' ? 2 : 1;
  return Math.min(end + 1, text.length);
};

/**
 * Comments become blank space of the same shape, so every later index still names the right line. SCSS adds
 * `//` line comments to CSS's block comments; a `//` inside a string or an unquoted `url(...)` is not one.
 */
function blankComments(css) {
  let out = '';
  for (let at = 0, end; at < css.length; at = end) {
    if (css.startsWith('/*', at)) {
      end = css.indexOf('*/', at + 2);
      end = end < 0 ? css.length : end + 2;
      out += blank(css.slice(at, end));
      continue;
    }
    if (css.startsWith('//', at)) {
      end = css.indexOf('\n', at);
      if (end < 0) end = css.length;
      out += blank(css.slice(at, end));
      continue;
    }
    if (css[at] === '"' || css[at] === "'") end = stringEnd(css, at);
    else if (/^url\(\s*[^\s'")]/i.test(css.slice(at, at + 64))) {
      end = css.indexOf(')', at);
      end = end < 0 ? css.length : end + 1;
    } else end = at + 1;
    out += css.slice(at, end);
  }
  return out;
}

/** A selector list split on its own commas, not those inside `:is(...)` or `:not(...)`. */
function selectorList(list) {
  const parts = [];
  let depth = 0;
  let start = 0;
  for (let at = 0; at < list.length; at++) {
    if (list[at] === '(') depth++;
    else if (list[at] === ')') depth--;
    else if (list[at] === ',' && depth === 0) {
      parts.push(list.slice(start, at).trim());
      start = at + 1;
    }
  }
  parts.push(list.slice(start).trim());
  return parts;
}

/** A nested selector written out against its parent, as Sass does: `&` is the parent, and no `&` is a descendant. */
function nestedSelector(parent, own) {
  if (parent === null) return own;
  const parents = selectorList(parent);
  return selectorList(own)
    .flatMap((child) => parents.map((p) => (child.includes('&') ? child.replaceAll('&', p) : `${p} ${child}`)))
    .join(', ');
}

/**
 * Each rule of a stylesheet: its full selector list, its own declarations, and the line it starts on. A nested
 * rule is reported under the selector Sass would write for it, and is blanked out of its parent's declarations;
 * an at-rule nested in a rule (`@media` inside `:host`) is transparent, so what it declares is the rule's own.
 */
export function cssRules(css) {
  const text = blankComments(css);
  const rules = [];
  const open = [];
  const own = (frame, to) => {
    let body = text.slice(frame.bodyAt, to);
    for (const [from, until] of frame.blanks) {
      const a = from - frame.bodyAt;
      const b = until - frame.bodyAt;
      body = body.slice(0, a) + blank(body.slice(a, b)) + body.slice(b);
    }
    return body;
  };
  let start = 0;
  for (let at = 0; at < text.length; at++) {
    const c = text[at];
    if (c === '"' || c === "'") {
      at = stringEnd(text, at) - 1;
    } else if (c === '#' && text[at + 1] === '{') {
      // Interpolation, `#{$name}`: part of a selector or value, not a block.
      const end = text.indexOf('}', at);
      at = end < 0 ? text.length : end;
    } else if (c === ';') {
      start = at + 1;
    } else if (c === '{') {
      const prelude = text.slice(start, at);
      const lead = prelude.length - prelude.trimStart().length;
      const parent = open.at(-1) ?? null;
      const inherited = parent ? parent.selector : null;
      const atRule = prelude.trimStart().startsWith('@');
      open.push({
        atRule,
        name: prelude.trim(),
        selector: atRule ? inherited : nestedSelector(inherited, prelude.trim()),
        start,
        bodyAt: at + 1,
        line: lineAt(text, start + lead),
        blanks: [],
      });
      start = at + 1;
    } else if (c === '}') {
      start = at + 1;
      const frame = open.pop();
      if (!frame) continue;
      const parent = open.at(-1);
      if (frame.atRule && frame.selector !== null && parent) {
        // Transparent: only the at-rule's own prelude and braces, and the rules inside it, leave the parent.
        parent.blanks.push([frame.start, frame.bodyAt], [at, at + 1], ...frame.blanks);
        continue;
      }
      const body = own(frame, at);
      if (!frame.atRule || body.trim() !== '') {
        rules.push({
          selector: frame.atRule ? frame.name : frame.selector,
          body,
          line: frame.line,
          bodyAt: frame.bodyAt,
          text,
        });
      }
      parent?.blanks.push([frame.start, at + 1]);
    }
  }
  // A rule closes after the rules nested in it; report them in the order they are written.
  return rules.sort((a, b) => a.bodyAt - b.bodyAt);
}

/** `frame`: declarations a routed page makes on `:host` itself — `:host`, `:host(...)`, or either in a list. */
export function frameViolations(css) {
  const found = [];
  for (const rule of cssRules(css)) {
    const targetsHost = rule.selector.split(',').some((selector) => /^:host(\([^)]*\))?$/.test(selector.trim()));
    if (!targetsHost) continue;
    for (const declaration of rule.body.matchAll(FRAME_PROPERTIES)) {
      found.push({
        rule: 'frame',
        line: lineAt(rule.text, rule.bodyAt + declaration.index + declaration[0].indexOf(declaration[1])),
        what: `:host sets ${declaration[1]}`,
      });
    }
  }
  return found;
}

/**
 * `enter` (AU.3.4): the entrance belongs to `cpEnter` — the three entrance tokens, the `cp-enter` keyframes it
 * animates, and the `data-cp-enter` it marks an arriving element with. Read over the whole sheet rather than
 * rule by rule, because a `@keyframes` block's own name is not an innermost rule. It does not claim every
 * fade-and-rise: a tab panel's switch and the public hero's collage are motion about something else.
 */
const ENTRANCE = /--cp-(?:duration-enter|enter-distance|enter-stagger)|data-cp-enter|cp-enter(?![\w-])/g;

/** Everything a feature stylesheet redraws that the library or the shell owns. */
export function stylesheetViolations(css) {
  const found = [];
  const sheet = blankComments(css);
  for (const m of sheet.matchAll(ENTRANCE)) {
    found.push({ rule: 'enter', line: lineAt(sheet, m.index), what: `animates an entrance of its own with ${m[0]}` });
  }
  for (const rule of cssRules(css)) {
    for (const [id, names] of Object.entries(OWNED_CLASSES)) {
      for (const name of names) {
        if (new RegExp(`\\.${name}(?![\\w-])`).test(rule.selector))
          found.push({ rule: id, line: rule.line, what: `styles .${name}` });
      }
    }
    if (/\[type=["']?search["']?\]/.test(rule.selector))
      found.push({ rule: 'toolbar-field', line: rule.line, what: 'styles input[type=search]' });
    // The head lays out what is projected into it; a feature that styles its own [cpPageHead…] slot redraws the head.
    for (const m of rule.selector.matchAll(/\[(cpPageHead\w+)\]/g))
      found.push({ rule: 'page-head', line: rule.line, what: `styles [${m[1]}]` });
    const at = (index) => lineAt(rule.text, rule.bodyAt + index);
    for (const m of rule.body.matchAll(/repeat\(\s*auto-(?:fill|fit)/g))
      found.push({ rule: 'card-grid', line: at(m.index), what: 'lays out an auto-filling grid' });
    for (const m of rule.body.matchAll(/position\s*:\s*fixed/g))
      found.push({ rule: 'drawer', line: at(m.index), what: 'sets position: fixed' });
    for (const m of rule.body.matchAll(/translateX\(/g))
      found.push({ rule: 'drawer', line: at(m.index), what: 'slides with translateX' });
    // `skeleton`: a placeholder of a feature's own, by its name (`.skeleton`, `.skeleton-row`, `.shimmer`), by the
    // library's skeleton tokens or sweep, or by a keyframe that moves a background — a shimmer whatever it is called.
    for (const m of rule.selector.matchAll(/\.((?:skeleton|shimmer)[\w-]*)/g))
      found.push({ rule: 'skeleton', line: rule.line, what: `styles .${m[1]}` });
    for (const m of rule.body.matchAll(/--cp-skeleton-[\w-]+|cp-skeleton-sweep/g))
      found.push({ rule: 'skeleton', line: at(m.index), what: `draws with ${m[0]}` });
    if (
      /^(from|to|[\d.]+%)(\s*,\s*(from|to|[\d.]+%))*$/.test(rule.selector) &&
      /background-position\s*:/.test(rule.body)
    ) {
      found.push({ rule: 'skeleton', line: rule.line, what: 'animates a background-position shimmer' });
    }
    // `tone`: the route owns a tool's tone (AU.2.1). A part reads --cp-tone*; a feature never sets it.
    if (/\[data-cp-tone/.test(rule.selector))
      found.push({ rule: 'tone', line: rule.line, what: 'selects on [data-cp-tone]' });
    for (const m of rule.body.matchAll(/(--cp-tone[\w-]*)\s*:/g))
      found.push({ rule: 'tone', line: at(m.index), what: `declares ${m[1]}` });
    // The panel tokens are cp-media-card's (AU.2.6): a feature reading them is drawing a no-picture panel of its own.
    for (const m of rule.body.matchAll(/var\(\s*--cp-tone-panel/g))
      found.push({ rule: 'illustration', line: at(m.index), what: 'draws a no-picture panel from --cp-tone-panel' });
  }
  return found;
}

/** What any template under the shell may not hold. */
/** A feature's own folder, where a template draws no picture of its own. */
const FEATURE = /[\\/]src[\\/]app[\\/]features[\\/]/;

/**
 * `illustration`, for a feature's template: an inline `<svg>` is a drawing the feature made for itself, which is
 * `cp-illustration`'s to draw (AU.2.4). Comments are left out; a picture of the creator's is an `<img>`, not this.
 */
export function featureTemplateViolations(html) {
  const markup = html.replace(/<!--[\s\S]*?-->/g, (comment) => comment.replace(/[^\n]/g, ' '));
  return [...markup.matchAll(/<svg[\s>]/g)].map((m) => ({
    rule: 'illustration',
    line: lineAt(markup, m.index),
    what: 'draws an inline <svg>',
  }));
}

export function templateViolations(html) {
  const found = [];
  const markup = html.replace(/<!--[\s\S]*?-->/g, (comment) => comment.replace(/[^\n]/g, ' '));
  for (const m of markup.matchAll(/<h1[\s>]/g))
    found.push({ rule: 'one-h1', line: lineAt(markup, m.index), what: 'renders an <h1>' });
  for (const m of markup.matchAll(/data-cp-tone/g))
    found.push({ rule: 'tone', line: lineAt(markup, m.index), what: 'sets data-cp-tone' });
  for (const m of markup.matchAll(/data-cp-enter/g))
    found.push({ rule: 'enter', line: lineAt(markup, m.index), what: 'sets data-cp-enter' });
  for (const m of markup.matchAll(/\[style\.(--cp-tone[\w-]*)|(--cp-tone[\w-]*)\s*:/g)) {
    found.push({ rule: 'tone', line: lineAt(markup, m.index), what: `declares ${m[1] ?? m[2]} in a style` });
  }
  for (const m of markup.matchAll(/type=["']search["']/g)) {
    // A search with a visible label and a hint is a form field, and `cp-field` draws that; the toolbar's
    // search has neither and is `cp-search-field`. What neither excuses is a search box no library part drew.
    const before = markup.slice(0, m.index);
    const inside = (tag) => before.lastIndexOf(`<${tag}`) > before.lastIndexOf(`</${tag}>`);
    if (!inside('cp-search-field') && !inside('cp-field')) {
      found.push({
        rule: 'toolbar-field',
        line: lineAt(markup, m.index),
        what: 'has a search input outside cp-search-field and cp-field',
      });
    }
  }
  // `generated` (AU.3.1): an element whose whole text is a label for generated output is a hand-drawn label.
  // A sentence that mentions one is prose, and a record's kind bound from code ({{ kindLabel() }}) is not text here.
  for (const m of markup.matchAll(GENERATED_LABEL)) {
    found.push({ rule: 'generated', line: lineAt(markup, m.index), what: `writes its own "${m[1]}" label` });
  }
  found.push(...loadingViolations(markup));
  return found;
}

/**
 * The words a sentence starts with when it stands in for something on its way (AU.7). A sentence about what the
 * creator has just done — "Saving…", "Asking…", "Writing this one again…" — reports an action, and is not one.
 */
const LOADING_VERB = '(?:Loading|Reading|Checking|Finding|Looking|Counting|Opening|Fetching|Comparing|Picking up|Taking you)\\b';
/**
 * Where such a sentence can be on screen: a text node (which may hold an apostrophe or an interpolation), or a
 * string in single or double quotes (an interpolation's or a binding's), each read whole.
 */
const LOADING_SENTENCES = [
  new RegExp(`>(\\s*(${LOADING_VERB}[^<]*?…)\\s*)<`, 'g'),
  new RegExp(`'(${LOADING_VERB}[^'\\n]*…)'`, 'g'),
  new RegExp(`"(${LOADING_VERB}[^"\\n]*…)"`, 'g'),
];
const VOID_ELEMENTS = new Set(['area', 'base', 'br', 'col', 'embed', 'hr', 'img', 'input', 'link', 'meta', 'source', 'track', 'wbr']);
/** The hidden-status class, as a whole class name: not `not-cp-sr-only`. */
const SR_ONLY = /\bclass=["'](?:[^"']*\s)?cp-sr-only(?=[\s"'])/;

/** Every element a position in a template sits inside, outermost first, each with its tag and attributes. */
function enclosingElements(markup, index) {
  const open = [];
  for (const m of markup.slice(0, index).matchAll(/<(\/?)([a-zA-Z][\w-]*)((?:[^>"']|"[^"]*"|'[^']*')*?)(\/?)>/g)) {
    const [, closing, tag, attributes, selfClosing] = m;
    if (closing) {
      const at = open.map((element) => element.tag).lastIndexOf(tag);
      if (at >= 0) open.splice(at);
    } else if (!selfClosing && !VOID_ELEMENTS.has(tag.toLowerCase())) {
      open.push({ tag, attributes });
    }
  }
  return open;
}

/**
 * `loading` (AU.7, APP-003): a sentence saying something is on its way, where the reader can see it. Inside a
 * `.cp-sr-only` element it is the one hidden status a loading area keeps, and is fine. As a button's own label it
 * is about what the button is doing: only a bare "Loading…" fails there, because "Loading more…" is the pager's
 * word and "Checking…" on a Check again button is the creator's own action. Text bound from code is not seen here.
 */
export function loadingViolations(markup) {
  const found = [];
  const candidates = LOADING_SENTENCES.flatMap((pattern) =>
    [...markup.matchAll(pattern)].map((m) => {
      const sentence = m.length > 2 ? m[2] : m[1];
      return { sentence: sentence.trim(), at: m.index + m[0].indexOf(sentence) };
    }),
  );
  for (const { sentence, at } of candidates) {
    // Inside the hidden status at any depth (a span within it included), it is the one status, and fine.
    const around = enclosingElements(markup, at);
    if (around.some((element) => SR_ONLY.test(element.attributes))) continue;
    const isButton = around.some((element) => element.tag === 'button' || /\scpButton(?=[\s>=]|$)/.test(element.attributes));
    if (isButton && sentence !== 'Loading…') continue;
    if (!isButton && sentence === 'Loading more…') {
      found.push({ rule: 'loading', line: lineAt(markup, at), what: 'says "Loading more…" outside the button that loads it' });
      continue;
    }
    found.push({ rule: 'loading', line: lineAt(markup, at), what: `shows "${sentence}" in place of what is coming` });
  }
  return found;
}

/** The words a feature used to mark generated output before `cp-generated-label`, and the ones it says now. */
const GENERATED_LABEL =
  />\s*(Drafted by AI|Made by AI|Read by AI|Written by AI|Written for you|AI[ -]generated|AI[ -]suggested|AI draft|Generated(?: draft)?)\s*</gi;

/** What a routed page's own template must and must not hold. */
export function pageTemplateViolations(html) {
  const found = [];
  const markup = html.replace(/<!--[\s\S]*?-->/g, (comment) => comment.replace(/[^\n]/g, ' '));
  if (!/<cp-page-head[\s>]/.test(markup)) found.push({ rule: 'page-head', line: 1, what: 'has no <cp-page-head>' });
  for (const m of markup.matchAll(/<header[\s>]/g))
    found.push({ rule: 'page-head', line: lineAt(markup, m.index), what: 'renders its own <header>' });
  return found;
}

const MEASURE_PROPERTIES = /(?:^|[;{\s])(max-width|max-inline-size)\s*:\s*([^;]+)/g;
const PADDING_PROPERTIES = /(?:^|[;{\s])(padding(?:-inline|-left|-right)?)\s*:\s*([^;]+)/g;
/** A width a public page may frame a section with: one of its three, or the prose measure. */
const PUBLIC_WIDTH = /^var\(--cp-(?:public-width(?:-narrow|-wide)?|measure-prose)\)$/;
const LITERAL_LENGTH = /\d(?:\.\d+)?(?:rem|em|px|ch|vw|vh)/;

/** The inline part of a `padding` shorthand: the only value, the second, or the second and the fourth. */
function inlinePadding(value) {
  const parts = value.trim().split(/\s+(?![^(]*\))/);
  if (parts.length === 1) return [parts[0]];
  return parts.length === 4 ? [parts[1], parts[3]] : [parts[1]];
}

/**
 * `public-frame`, for one public stylesheet. A rule that caps a width and pads its sides is a section's frame,
 * and takes both from the public tokens. Outside the hero, no width is capped with a literal length, and no
 * section pads, caps or offsets its own host — the hairline between sections is drawn against the host.
 */
export function publicStylesheetViolations(css, { hero = false } = {}) {
  const found = hero ? [] : frameViolations(css).map((f) => ({ ...f, rule: 'public-frame' }));
  for (const rule of cssRules(css)) {
    const at = (index) => lineAt(rule.text, rule.bodyAt + index);
    const widths = [...rule.body.matchAll(MEASURE_PROPERTIES)];
    const sides = [...rule.body.matchAll(PADDING_PROPERTIES)].flatMap((m) =>
      (m[1] === 'padding' ? inlinePadding(m[2]) : [m[2].trim()]).map((value) => ({
        value,
        line: at(m.index + m[0].indexOf(m[1])),
      })),
    );
    const frames = widths.length > 0 && sides.length > 0;
    for (const m of widths) {
      const value = m[2].trim();
      const line = at(m.index + m[0].indexOf(m[1]));
      if (frames && !PUBLIC_WIDTH.test(value))
        found.push({ rule: 'public-frame', line, what: `frames a section at ${value} rather than a public width` });
      else if (!frames && !hero && LITERAL_LENGTH.test(value.replace(/var\([^)]*\)/g, '')))
        found.push({ rule: 'public-frame', line, what: `caps a measure at the literal ${value}` });
    }
    if (frames) {
      for (const side of sides.filter((s) => s.value !== 'var(--cp-public-gutter)' && s.value !== '0')) {
        found.push({
          rule: 'public-frame',
          line: side.line,
          what: `frames a section with the gutter ${side.value} rather than --cp-public-gutter`,
        });
      }
    }
  }
  return found;
}

/** `public-h1` and `public-control`, for one public template. `headline` marks the template that owns the page's `h1`. */
export function publicTemplateViolations(html, { headline = false } = {}) {
  const found = [];
  const markup = html.replace(/<!--[\s\S]*?-->/g, (comment) => comment.replace(/[^\n]/g, ' '));
  const h1s = [...markup.matchAll(/<h1[\s>]/g)];
  if (headline && h1s.length !== 1)
    found.push({ rule: 'public-h1', line: 1, what: `renders ${h1s.length} <h1> rather than the page's one` });
  if (!headline)
    for (const m of h1s)
      found.push({
        rule: 'public-h1',
        line: lineAt(markup, m.index),
        what: 'renders an <h1> that is not the page headline',
      });
  for (const m of markup.matchAll(/<button\b[^>]*>/g)) {
    if (!/\scpButton(?=[\s>=])/.test(m[0]))
      found.push({
        rule: 'public-control',
        line: lineAt(markup, m.index),
        what: 'renders a <button> that is not cpButton',
      });
  }
  return found;
}

const read = (file) => readFileSync(file, 'utf8');
const imports = (source, loader) =>
  [...source.matchAll(new RegExp(`${loader}:\\s*\\(\\)\\s*=>\\s*import\\(\\s*['"]([^'"]+)['"]`, 'g'))].map((m) => m[1]);

/** Component files reachable from a route table, following lazy child tables. */
function routedComponents(routesFile, seen = new Set()) {
  if (seen.has(routesFile)) return [];
  seen.add(routesFile);
  const source = read(routesFile);
  const here = dirname(routesFile);
  return [
    ...imports(source, 'loadComponent').map((path) => resolve(here, `${path}.ts`)),
    ...imports(source, 'loadChildren').flatMap((path) => routedComponents(resolve(here, `${path}.ts`), seen)),
  ];
}

/** A component's inline `styles` and inline `template`, each with the line it starts on in the `.ts` file. */
export function inlineParts(source) {
  const parts = {};
  const styles = source.match(/styles:\s*(\[[\s\S]*?\]|`[\s\S]*?`|'[^']*')\s*,?\s*\n/);
  // The array brackets and string delimiters go, or the first selector would read "[`:host" and match nothing.
  if (styles)
    parts.styles = {
      text: styles[1].replace(/^\[|\]$/g, ' ').replace(/[`'"]/g, ' '),
      line: lineAt(source, styles.index + styles[0].indexOf(styles[1])),
    };
  const template = source.match(/template:\s*`([\s\S]*?)`/);
  if (template)
    parts.template = { text: template[1], line: lineAt(source, template.index + template[0].indexOf('`') + 1) };
  return parts;
}

/**
 * `tone`, for a component's own code: a feature that sets a tone from TypeScript — a `host` binding,
 * `setAttribute`, `Renderer2`, `style.setProperty` — rather than from its route. Comments and the inline
 * template and styles are left out: the first say nothing, and the other two are read by their own rules.
 */
export function scriptViolations(source) {
  const found = [];
  const inline = inlineParts(source);
  let code = source.replace(/\/\*[\s\S]*?\*\/|(^|[^:'"`])\/\/[^\n]*/g, (comment) => comment.replace(/[^\n]/g, ' '));
  for (const part of [inline.template, inline.styles]) {
    if (part) code = code.replace(part.text, part.text.replace(/[^\n]/g, ' '));
  }
  for (const m of code.matchAll(/data-cp-tone|setProperty\(\s*['"`](--cp-tone[\w-]*)/g)) {
    found.push({
      rule: 'tone',
      line: lineAt(code, m.index),
      what: m[1] ? `sets ${m[1]} from code` : 'sets data-cp-tone from code',
    });
  }
  for (const m of code.matchAll(/data-cp-enter/g)) {
    found.push({ rule: 'enter', line: lineAt(code, m.index), what: 'sets data-cp-enter from code' });
  }
  return found;
}

/**
 * Sorts what was found by the exemption list: what still stands, and which exemptions excuse nothing any
 * more. An exemption covers one rule in one file and nothing wider.
 */
export function judge(failures, exemptions) {
  const covers = (e, f) => e.file === f.file && e.rule === f.rule;
  return {
    standing: failures.filter((f) => !exemptions.some((e) => covers(e, f))),
    stale: exemptions.filter((e) => !failures.some((f) => covers(e, f))),
  };
}

function filesUnder(dir) {
  return readdirSync(dir, { withFileTypes: true }).flatMap((entry) => {
    const path = join(dir, entry.name);
    return entry.isDirectory() ? filesUnder(path) : [path];
  });
}

function run() {
  if (!read(appRoutes).includes(SHELL)) {
    console.error(`check-page-contract: ${relative(root, appRoutes)} no longer mounts ${SHELL}; update this check.`);
    return 1;
  }

  const name = (file) => relative(root, file).replace(/\\/g, '/');
  const failures = [];
  const report = (file, offset, found) =>
    failures.push(...found.map((f) => ({ ...f, file: name(file), line: f.line + offset })));

  const pages = [...new Set(routedComponents(appRoutes))].filter(
    // Outside the shell, or the shell itself: none of these sits inside the frame.
    (file) => existsSync(file) && !OUTSIDE_SHELL.test(file) && !file.endsWith('app-shell.component.ts'),
  );
  if (pages.length === 0) {
    console.error('check-page-contract: found no routed components, so nothing was checked. Update this check.');
    return 1;
  }

  for (const page of pages) {
    const source = read(page);
    const here = dirname(page);
    const inline = inlineParts(source);
    for (const m of source.matchAll(/styleUrls?:\s*(\[[^\]]*\]|['"][^'"]+['"])/g)) {
      for (const url of m[1].matchAll(/['"]([^'"]+)['"]/g))
        report(resolve(here, url[1]), 0, frameViolations(read(resolve(here, url[1]))));
    }
    if (inline.styles) report(page, inline.styles.line - 1, frameViolations(inline.styles.text));
    const templateUrl = source.match(/templateUrl:\s*['"]([^'"]+)['"]/);
    if (templateUrl)
      report(resolve(here, templateUrl[1]), 0, pageTemplateViolations(read(resolve(here, templateUrl[1]))));
    else if (inline.template) report(page, inline.template.line - 1, pageTemplateViolations(inline.template.text));
  }

  const files = filesUnder(appDir).filter((file) => !OUTSIDE_SHELL.test(file) && !file.endsWith('.spec.ts'));
  for (const file of files) {
    // A feature's own templates also may not draw: the shell's nav icons are chrome and sit outside features/.
    const feature = FEATURE.test(file);
    const markup = (html) => [...templateViolations(html), ...(feature ? featureTemplateViolations(html) : [])];
    if (file.endsWith('.css')) report(file, 0, stylesheetViolations(read(file)));
    else if (file.endsWith('.html')) report(file, 0, markup(read(file)));
    else if (file.endsWith('.ts')) {
      const inline = inlineParts(read(file));
      if (inline.styles) report(file, inline.styles.line - 1, stylesheetViolations(inline.styles.text));
      if (inline.template) report(file, inline.template.line - 1, markup(inline.template.text));
      report(file, 0, scriptViolations(read(file)));
    }
  }

  // The public pages: every part rule the shell's pages answer to, and their own shape in place of the frame.
  const publicFiles = filesUnder(appDir).filter((file) => PUBLIC_PAGE.test(file) && !file.endsWith('.spec.ts'));
  if (publicFiles.filter((file) => HEADLINE.test(file)).length !== 2) {
    console.error(
      'check-page-contract: did not find both public headline templates, so the public pages were not checked. Update this check.',
    );
    return 1;
  }
  for (const file of publicFiles) {
    const hero = HERO.test(file);
    const markupFound = (html) => [
      ...templateViolations(html).filter((f) => f.rule !== 'one-h1'),
      ...publicTemplateViolations(html, { headline: HEADLINE.test(file) }),
    ];
    const styleFound = (css) => [...stylesheetViolations(css), ...publicStylesheetViolations(css, { hero })];
    if (file.endsWith('.css')) report(file, 0, styleFound(read(file)));
    else if (file.endsWith('.html')) report(file, 0, markupFound(read(file)));
    else if (file.endsWith('.ts')) {
      const inline = inlineParts(read(file));
      if (inline.styles) report(file, inline.styles.line - 1, styleFound(inline.styles.text));
      if (inline.template) report(file, inline.template.line - 1, markupFound(inline.template.text));
    }
  }

  const { standing, stale } = judge(failures, EXEMPTIONS);

  if (standing.length > 0 || stale.length > 0) {
    const lines = ['check-page-contract: the page contract is broken.'];
    for (const id of Object.keys(RULES)) {
      const mine = standing.filter((f) => f.rule === id);
      if (mine.length === 0) continue;
      lines.push('', `[${id}] ${RULES[id]}`, ...mine.map((f) => `  ${f.file}:${f.line}: ${f.what}`));
    }
    if (stale.length > 0) {
      lines.push(
        '',
        'Exemptions that excuse nothing any more — delete them from EXEMPTIONS:',
        ...stale.map((e) => `  ${e.file} [${e.rule}]`),
      );
    }
    lines.push(
      '',
      'Fix the file, or list it in EXEMPTIONS in scripts/check-page-contract.mjs with the reason.',
      'See src/web/DESIGN-SYSTEM.md, "Composing a page".',
    );
    console.error(lines.join('\n'));
    return 1;
  }

  console.log(
    `check-page-contract: ${pages.length} routed pages, ${files.length} files and ${publicFiles.length} public-page files hold to the page contract (${EXEMPTIONS.length} exemptions).`,
  );
  return 0;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) process.exit(run());
