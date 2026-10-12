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
// under `src/app`, because a part redrawn in a child component is still redrawn. Sign-in, sign-up,
// confirm-email, the landing page and the showcase sit outside the shell and are not judged.
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

/** What each rule protects, printed once under its failures. */
export const RULES = {
  frame: "The shell's page frame owns the gutter and the width. Remove the declaration; a page that needs another width asks with `data: { pageWidth: 'reading' | 'wide' }` on its route.",
  'one-h1': '`cp-page-head` prints the one `h1` on a page. Give the page a `cp-page-head` and start everything else at `h2`.',
  'page-head': 'A routed page opens with `cp-page-head` (and `a[cpBackLink]` for its breadcrumb); it draws no header of its own.',
  'card-grid': 'A list of cards is `ul[cpCardGrid]`; short fields in a line are `cp-field-row`; term-and-value pairs are `dl[cpFacts]`.',
  facts: 'Term-and-value pairs are `dl[cpFacts]` of `div[cpFact]`, `stacked` or `inline`.',
  'toolbar-field': "A toolbar's search box is the feature's own `<input type=\"search\">` inside `cp-search-field` (inside `cp-field` where it has a visible label); a toolbar filter or sort is a `<select>` inside `cp-select-field`.",
  stepper: 'The steps of a flow are `cp-stepper`.',
  chip: 'A filter with a few fixed answers is `button[cpChip]` in a `[cpChipGroup]`.',
  drawer: 'A panel over the page is `cp-drawer` (or `cp-dialog`); a feature positions nothing against the viewport.',
  utilities: '`.cp-muted`, `.cp-actions` and `.cp-disclosure` live in global.css. Use the class; do not redeclare it or keep a private `.muted` or `.actions` beside it.',
};

/**
 * Every file excused from a rule, and why. `file` is relative to `src/web`, with forward slashes.
 * Granting one costs a line here that a reviewer reads.
 */
export const EXEMPTIONS = [
  {
    file: 'src/app/shell/app-shell.component.css',
    rule: 'drawer',
    reason: "The sidebar and the top bar are the shell's own chrome: fixed to the viewport, and the sidebar slides in at a narrow width. Nothing else may be.",
  },
  {
    file: 'src/app/features/recipes/recipe-kitchen-view.component.css',
    rule: 'facts',
    reason: 'The one page that prints. Its times and yield are set large to be read from across a kitchen and reset in points for paper, and UI.6 held it to printing exactly as it did.',
  },
  {
    file: 'src/app/features/recipes/recipe-kitchen-view.component.css',
    rule: 'stepper',
    reason: "`.step-list` is the recipe's method — the steps a cook follows — not progress through a flow.",
  },
  {
    file: 'src/app/features/recipes/recipe-kitchen-view.component.css',
    rule: 'page-head',
    reason: "Under @media print only: takes the page head's breadcrumb slot off the paper. cp-page-head has no print behaviour of its own.",
  },
  {
    file: 'src/app/features/recipes/recipe-kitchen-view.component.css',
    rule: 'utilities',
    reason: "Under @media print only: takes the page head's actions slot off the paper, in the same rule as the breadcrumb.",
  },
  {
    file: 'src/app/features/recipes/recipe-editor.component.css',
    rule: 'stepper',
    reason: "`.steps` is the list of the recipe's instruction steps being edited, not progress through a flow.",
  },
  {
    file: 'src/app/features/ai-usage/ai-usage.component.css',
    rule: 'card-grid',
    reason: '`.breakdowns` sets two `cp-list-shell` panels side by side, not a list of cards.',
  },
  {
    file: 'src/app/features/recipes/recipe-duplicate.component.css',
    rule: 'card-grid',
    reason: '`.summary` sets two short lists side by side — what a copy keeps and what it leaves — not a list of cards.',
  },
  {
    file: 'src/app/features/ai/recipe-first-draft-review.component.css',
    rule: 'card-grid',
    reason: "`.draft-fields-grid` reads a draft's short values across, each with its own edit control from a shared template. `dl[cpFacts]` has no across layout; give it one and this goes.",
  },
];

const FRAME_PROPERTIES = /(?:^|[;{\s])((?:padding|margin)(?:-[a-z-]+)?|max-width|width|inline-size|max-inline-size)\s*:/g;

/** Class names a feature stylesheet may not style, by the rule that owns them. */
const OWNED_CLASSES = {
  'page-head': ['page-head', 'page-header', 'page-title', 'back', 'breadcrumb'],
  'card-grid': ['grid', 'card-grid', 'cards'],
  facts: ['facts', 'fact'],
  'toolbar-field': ['search-field', 'select-field'],
  stepper: ['step-list', 'steps', 'stepper'],
  chip: ['chip', 'chips'],
  utilities: ['cp-muted', 'cp-actions', 'cp-disclosure', 'muted', 'actions'],
};

const lineAt = (text, index) => text.slice(0, index).split('\n').length;
/** Comments become blank space of the same shape, so every later index still names the right line. */
const blankComments = (css) => css.replace(/\/\*[\s\S]*?\*\//g, (comment) => comment.replace(/[^\n]/g, ' '));

/** Each innermost rule of a stylesheet: its selector list, its declarations, and the line it starts on. */
export function cssRules(css) {
  const stripped = blankComments(css);
  return [...stripped.matchAll(/([^{}]+)\{([^{}]*)\}/g)].map((m) => {
    const lead = m[1].length - m[1].trimStart().length;
    return { selector: m[1].trim(), body: m[2], line: lineAt(stripped, m.index + lead), bodyAt: m.index + m[1].length + 1, text: stripped };
  });
}

/** `frame`: declarations a routed page makes on `:host` itself — `:host`, `:host(...)`, or either in a list. */
export function frameViolations(css) {
  const found = [];
  for (const rule of cssRules(css)) {
    const targetsHost = rule.selector.split(',').some((selector) => /^:host(\([^)]*\))?$/.test(selector.trim()));
    if (!targetsHost) continue;
    for (const declaration of rule.body.matchAll(FRAME_PROPERTIES)) {
      found.push({ rule: 'frame', line: lineAt(rule.text, rule.bodyAt + declaration.index + declaration[0].indexOf(declaration[1])), what: `:host sets ${declaration[1]}` });
    }
  }
  return found;
}

/** Everything a feature stylesheet redraws that the library or the shell owns. */
export function stylesheetViolations(css) {
  const found = [];
  for (const rule of cssRules(css)) {
    for (const [id, names] of Object.entries(OWNED_CLASSES)) {
      for (const name of names) {
        if (new RegExp(`\\.${name}(?![\\w-])`).test(rule.selector)) found.push({ rule: id, line: rule.line, what: `styles .${name}` });
      }
    }
    if (/\[type=["']?search["']?\]/.test(rule.selector)) found.push({ rule: 'toolbar-field', line: rule.line, what: 'styles input[type=search]' });
    const at = (index) => lineAt(rule.text, rule.bodyAt + index);
    for (const m of rule.body.matchAll(/repeat\(\s*auto-(?:fill|fit)/g)) found.push({ rule: 'card-grid', line: at(m.index), what: 'lays out an auto-filling grid' });
    for (const m of rule.body.matchAll(/position\s*:\s*fixed/g)) found.push({ rule: 'drawer', line: at(m.index), what: 'sets position: fixed' });
    for (const m of rule.body.matchAll(/translateX\(/g)) found.push({ rule: 'drawer', line: at(m.index), what: 'slides with translateX' });
  }
  return found;
}

/** What any template under the shell may not hold. */
export function templateViolations(html) {
  const found = [];
  const markup = html.replace(/<!--[\s\S]*?-->/g, (comment) => comment.replace(/[^\n]/g, ' '));
  for (const m of markup.matchAll(/<h1[\s>]/g)) found.push({ rule: 'one-h1', line: lineAt(markup, m.index), what: 'renders an <h1>' });
  for (const m of markup.matchAll(/type=["']search["']/g)) {
    // A search with a visible label and a hint is a form field, and `cp-field` draws that; the toolbar's
    // search has neither and is `cp-search-field`. What neither excuses is a search box no library part drew.
    const before = markup.slice(0, m.index);
    const inside = (tag) => before.lastIndexOf(`<${tag}`) > before.lastIndexOf(`</${tag}>`);
    if (!inside('cp-search-field') && !inside('cp-field')) {
      found.push({ rule: 'toolbar-field', line: lineAt(markup, m.index), what: 'has a search input outside cp-search-field and cp-field' });
    }
  }
  return found;
}

/** What a routed page's own template must and must not hold. */
export function pageTemplateViolations(html) {
  const found = [];
  const markup = html.replace(/<!--[\s\S]*?-->/g, (comment) => comment.replace(/[^\n]/g, ' '));
  if (!/<cp-page-head[\s>]/.test(markup)) found.push({ rule: 'page-head', line: 1, what: 'has no <cp-page-head>' });
  for (const m of markup.matchAll(/<header[\s>]/g)) found.push({ rule: 'page-head', line: lineAt(markup, m.index), what: 'renders its own <header>' });
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
  if (styles) parts.styles = { text: styles[1].replace(/^\[|\]$/g, ' ').replace(/[`'"]/g, ' '), line: lineAt(source, styles.index + styles[0].indexOf(styles[1])) };
  const template = source.match(/template:\s*`([\s\S]*?)`/);
  if (template) parts.template = { text: template[1], line: lineAt(source, template.index + template[0].indexOf('`') + 1) };
  return parts;
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
  const report = (file, offset, found) => failures.push(...found.map((f) => ({ ...f, file: name(file), line: f.line + offset })));

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
      for (const url of m[1].matchAll(/['"]([^'"]+)['"]/g)) report(resolve(here, url[1]), 0, frameViolations(read(resolve(here, url[1]))));
    }
    if (inline.styles) report(page, inline.styles.line - 1, frameViolations(inline.styles.text));
    const templateUrl = source.match(/templateUrl:\s*['"]([^'"]+)['"]/);
    if (templateUrl) report(resolve(here, templateUrl[1]), 0, pageTemplateViolations(read(resolve(here, templateUrl[1]))));
    else if (inline.template) report(page, inline.template.line - 1, pageTemplateViolations(inline.template.text));
  }

  const files = filesUnder(appDir).filter((file) => !OUTSIDE_SHELL.test(file) && !file.endsWith('.spec.ts'));
  for (const file of files) {
    if (file.endsWith('.css')) report(file, 0, stylesheetViolations(read(file)));
    else if (file.endsWith('.html')) report(file, 0, templateViolations(read(file)));
    else if (file.endsWith('.ts')) {
      const inline = inlineParts(read(file));
      if (inline.styles) report(file, inline.styles.line - 1, stylesheetViolations(inline.styles.text));
      if (inline.template) report(file, inline.template.line - 1, templateViolations(inline.template.text));
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
      lines.push('', 'Exemptions that excuse nothing any more — delete them from EXEMPTIONS:', ...stale.map((e) => `  ${e.file} [${e.rule}]`));
    }
    lines.push('', 'Fix the file, or list it in EXEMPTIONS in scripts/check-page-contract.mjs with the reason.', 'See src/web/DESIGN-SYSTEM.md, "Composing a page".');
    console.error(lines.join('\n'));
    return 1;
  }

  console.log(`check-page-contract: ${pages.length} routed pages and ${files.length} files hold to the page contract (${EXEMPTIONS.length} exemptions).`);
  return 0;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) process.exit(run());
