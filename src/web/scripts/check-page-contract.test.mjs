// Proves each rule of check-page-contract.mjs fires on the drift it names and stays quiet on the library's
// own way of doing the same thing. Run by `npm test` before the check itself: a rule that silently matches
// nothing would pass every page for ever.

import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';

import {
  EXEMPTIONS,
  RULES,
  frameViolations,
  inlineParts,
  judge,
  pageTemplateViolations,
  publicStylesheetViolations,
  publicTemplateViolations,
  featureTemplateViolations,
  scriptViolations,
  stylesheetViolations,
  templateViolations,
} from './check-page-contract.mjs';

const rules = (found) => found.map((f) => f.rule);

test('frame: a routed page may not pad, cap or offset its own host', () => {
  for (const declaration of ['padding: 1rem', 'padding-inline: 1rem', 'max-width: 80rem', 'margin: 0 auto', 'width: 60rem', 'max-inline-size: 60rem']) {
    assert.deepEqual(rules(frameViolations(`:host { display: block; ${declaration}; }`)), ['frame'], declaration);
  }
  assert.deepEqual(rules(frameViolations(':host(.wide), .x { padding: 0; }')), ['frame']);
});

test('frame: a host may still be a block, a grid or a container, and children may pad themselves', () => {
  assert.deepEqual(frameViolations(':host { display: grid; gap: 1rem; min-width: 0; container: page / inline-size; }'), []);
  assert.deepEqual(frameViolations('.card { padding: 1rem; max-width: 40rem; }'), []);
  assert.deepEqual(frameViolations('/* :host { padding: 1rem; } */ :host { display: block; }'), []);
});

test('frame: names the line the declaration is on', () => {
  assert.equal(frameViolations(':host {\n  display: block;\n  padding: 1rem;\n}')[0].line, 3);
});

test('one-h1: no template prints its own h1', () => {
  assert.deepEqual(rules(templateViolations('<section>\n<h1 id="x">Recipes</h1></section>')), ['one-h1']);
  assert.deepEqual(templateViolations('<cp-page-head heading="Recipes" /><h2>Drafts</h2><!-- <h1>old</h1> -->'), []);
});

test('page-head: a routed page opens with cp-page-head and draws no header', () => {
  assert.deepEqual(rules(pageTemplateViolations('<header><p>Recipes</p></header>')), ['page-head', 'page-head']);
  assert.deepEqual(pageTemplateViolations('<cp-page-head heading="Recipes"></cp-page-head><cp-toolbar />'), []);
});

test('page-head: no stylesheet draws a page header or a back link', () => {
  // Written out rather than read from the script, so dropping a name from the script fails here.
  for (const name of ['page-head', 'page-header', 'page-title', 'back', 'breadcrumb', 'page-icon', 'head-icon', 'title-icon', 'head-media', 'page-media', 'title-media', 'masthead', 'title-row', 'head-picture', 'hero-image']) {
    assert.deepEqual(rules(stylesheetViolations(`.${name} { margin: 0; }`)), ['page-head'], name);
  }
  assert.deepEqual(stylesheetViolations('.back-note, .background { margin: 0; }'), []);
  // A feature styling a slot of the head redraws the head.
  assert.deepEqual(rules(stylesheetViolations('[cpPageHeadMedia] { border-radius: 0; } [cpPageHeadIcon] svg { width: 2rem; }')), ['page-head', 'page-head']);
  // The common words stay free: a card's own icon or a picture panel is not the head's.
  assert.deepEqual(stylesheetViolations('.icon { width: 1rem; } .media { aspect-ratio: 1; } .thumbnail { width: 4rem; }'), []);
});

test('card-grid: no stylesheet lays out its own grid of cards', () => {
  assert.deepEqual(rules(stylesheetViolations('.grid { display: grid; }')), ['card-grid']);
  assert.deepEqual(rules(stylesheetViolations('.tiles { grid-template-columns: repeat(auto-fill, minmax(10rem, 1fr)); }')), ['card-grid']);
  assert.deepEqual(rules(stylesheetViolations('.tiles { grid-template-columns: repeat( auto-fit, minmax(10rem, 1fr)); }')), ['card-grid']);
  assert.deepEqual(stylesheetViolations('.pair { display: grid; grid-template-columns: minmax(0, 1fr) auto; }'), []);
});

test('facts, stepper, chip and toolbar-field: the library owns each', () => {
  assert.deepEqual(rules(stylesheetViolations('.facts { margin: 0; } .fact dt { font-size: 1rem; }')), ['facts', 'facts']);
  assert.deepEqual(rules(stylesheetViolations('.step-list { margin: 0; }')), ['stepper']);
  assert.deepEqual(rules(stylesheetViolations('.chip { cursor: pointer; }')), ['chip']);
  assert.deepEqual(rules(stylesheetViolations('.board { display: flex; } .lane { flex: 1; }')), ['board', 'board']);
  assert.deepEqual(stylesheetViolations('.board-card-title { margin: 0; } .columns { display: grid; }'), []);
  assert.deepEqual(rules(stylesheetViolations('.search-field input { border: 0; }')), ['toolbar-field']);
  assert.deepEqual(rules(stylesheetViolations("input[type='search'] { border: 0; }")), ['toolbar-field']);
  assert.deepEqual(stylesheetViolations('.steps-disclosure summary { font-weight: 700; } .chip-note { margin: 0; }'), []);
});

test('stat-tile: a feature draws no figure tile of its own', () => {
  assert.deepEqual(rules(stylesheetViolations('.stat { font-size: 3rem; } .kpi-value { margin: 0; } .figure-tile { padding: 1rem; }')), ['stat-tile', 'stat-tile']);
  // A table's figure cell and a statistic named in prose are not tiles.
  assert.deepEqual(stylesheetViolations('.figure { font-variant-numeric: tabular-nums; } .status { color: red; }'), []);
});

test('option-card: a feature draws no option or concept card of its own', () => {
  assert.deepEqual(rules(stylesheetViolations('.concept-title { font-weight: 700; } .option-card { padding: 1rem; }')), ['option-card', 'option-card']);
  // A list of concepts is a grid the card-grid rule already owns, and an optional field is not an option card.
  assert.deepEqual(stylesheetViolations('.concept-list { gap: 1rem; } .optional { color: red; }'), []);
});

test('enter: a feature animates no entrance of its own', () => {
  assert.deepEqual(rules(stylesheetViolations('.result { animation: cp-enter 200ms; }')), ['enter']);
  assert.deepEqual(rules(stylesheetViolations('@keyframes cp-enter { from { opacity: 0; } }')), ['enter']);
  assert.deepEqual(rules(stylesheetViolations('.row { animation-duration: var(--cp-duration-enter); animation-delay: var(--cp-enter-stagger); translate: 0 var(--cp-enter-distance); }')), ['enter', 'enter', 'enter']);
  assert.deepEqual(rules(stylesheetViolations('[data-cp-enter] .row { opacity: 1; }')), ['enter']);
  assert.deepEqual(rules(templateViolations('<div data-cp-enter></div>')), ['enter']);
  assert.deepEqual(rules(scriptViolations("el.setAttribute('data-cp-enter', '');")), ['enter']);
  // A page may ask for the entrance; what it may not do is draw one. Other motion is not this rule's.
  assert.deepEqual(stylesheetViolations('.panel { animation: cp-panel-in var(--cp-duration-normal) var(--cp-ease); }'), []);
  assert.deepEqual(templateViolations('<ul [cpEnter]="ready()"></ul>'), []);
  assert.deepEqual(stylesheetViolations('/* --cp-enter-stagger is the library\u2019s */ .row { gap: 1rem; }'), []);
});

test('toolbar-field: a search input sits in cp-search-field, or in cp-field when it has a visible label', () => {
  assert.deepEqual(rules(templateViolations('<div class="search"><input type="search" /></div>')), ['toolbar-field']);
  assert.deepEqual(rules(templateViolations('<cp-search-field label="x"><input type="search" /></cp-search-field>\n<input type="search" />')), ['toolbar-field']);
  assert.deepEqual(templateViolations('<cp-search-field label="x"><input type="search" /></cp-search-field>'), []);
  assert.deepEqual(templateViolations('<cp-field label="Search"><input type="search" /></cp-field>'), []);
});

test('tone: a feature neither sets a tone nor declares the tone tokens; it may read them', () => {
  assert.deepEqual(rules(templateViolations('<section data-cp-tone="pink"></section>')), ['tone']);
  assert.deepEqual(rules(templateViolations('<div [attr.data-cp-tone]="tone()"></div>')), ['tone']);
  assert.deepEqual(rules(stylesheetViolations(':host { --cp-tone: var(--cp-pink); --cp-tone-text: red; }')), ['tone', 'tone']);
  assert.deepEqual(rules(stylesheetViolations('[data-cp-tone="pink"] .title { color: red; }')), ['tone']);
  assert.deepEqual(stylesheetViolations('.title { color: var(--cp-tone-text); border-color: var(--cp-tone); }'), []);
  assert.deepEqual(templateViolations('<p class="tone-note">A tone of voice.</p>'), []);
  assert.deepEqual(rules(templateViolations('<div style="--cp-tone: red"></div><div [style.--cp-tone-text]="x"></div>')), ['tone', 'tone']);
  assert.deepEqual(templateViolations('<div style="color: var(--cp-tone-text)"></div>'), []);
});

test('tone: nor sets one from code; a comment saying so, and an inline template read elsewhere, are not counted twice', () => {
  assert.deepEqual(rules(scriptViolations("@Component({ host: { '[attr.data-cp-tone]': 'tone()' } })")), ['tone']);
  assert.deepEqual(rules(scriptViolations("el.setAttribute('data-cp-tone', 'pink');")), ['tone']);
  assert.deepEqual(rules(scriptViolations("el.style.setProperty('--cp-tone', 'red');")), ['tone']);
  assert.deepEqual(scriptViolations("// the frame sets data-cp-tone\n/* and data-cp-tone here */ const x = getComputedStyle(el).getPropertyValue('--cp-tone');"), []);
  assert.deepEqual(scriptViolations('@Component({ template: `<div [attr.data-cp-tone]="t"></div>`, })'), []);
});

// The shell's exemption covers its whole template, so this holds it to the one binding it exists for: on the
// frame inside <main>, and nowhere the nav or the top bar could pick it up.
test('tone: the shell sets the tone once, on the page frame', () => {
  const shell = readFileSync(new URL('../src/app/shell/app-shell.component.html', import.meta.url), 'utf8').replace(/<!--[\s\S]*?-->/g, '');
  const uses = shell.split('\n').filter((line) => line.includes('data-cp-tone'));
  assert.equal(uses.length, 1, uses.join('\n'));
  assert.match(uses[0], /class="page-frame"[^>]*\[attr\.data-cp-tone\]="currentPageTone\(\)"/);
});

test('generated: a template writes no label for generated output of its own', () => {
  const labels = '<cp-badge tone="purple">Written for you</cp-badge><span>AI generated</span><b> Generated </b><i>Drafted by AI</i><span>Generated draft</span>' +
    '<cp-badge>AI suggested</cp-badge><span>AI Generated</span><em>Written For You</em>';
  assert.deepEqual(rules(templateViolations(labels)), Array(8).fill('generated'));
  // The library's label, a sentence that mentions one, a heading in the creator's terms and a kind bound from code.
  const fine = '<cp-generated-label kind="picture" /><p>These words were written for you from the recipe.</p>' +
    '<h3>What was written for you</h3><cp-badge>{{ kindLabel() }}</cp-badge><!-- <b>Generated</b> -->';
  assert.deepEqual(templateViolations(fine), []);
});

test('illustration: a feature draws no empty-state art of its own', () => {
  assert.deepEqual(rules(stylesheetViolations('.illustration { width: 7rem; } .empty-art svg { stroke: red; }')), ['illustration', 'illustration']);
  assert.deepEqual(stylesheetViolations('.illustration-credit { font-size: 12px; } .art-direction { margin: 0; }'), []);
  // A card with no picture takes cp-media-card's panel (AU.2.6), by class name or by its tokens.
  assert.deepEqual(rules(stylesheetViolations('.no-picture { aspect-ratio: 4 / 3; } .tile { background: var(--cp-tone-panel); }')), ['illustration', 'illustration']);
  assert.deepEqual(stylesheetViolations('.panel { padding: 1rem; } .placeholder { color: var(--cp-tone-text); }'), []);
  assert.deepEqual(rules(featureTemplateViolations('<div><svg viewBox="0 0 10 10"><path d="M0 0"/></svg></div>')), ['illustration']);
  assert.deepEqual(featureTemplateViolations('<!-- an <svg> once lived here --><cp-illustration name="recipe" /><img src="x.png" alt="">'), []);
});

test('skeleton: a feature draws no placeholder or shimmer of its own', () => {
  assert.deepEqual(rules(stylesheetViolations('.skeleton { height: 1rem; } .shimmer { opacity: .5; }')), ['skeleton', 'skeleton']);
  assert.deepEqual(rules(stylesheetViolations('@keyframes pulse { from { background-position: 100% 0; } to { background-position: 0 0; } }')), ['skeleton', 'skeleton']);
  assert.deepEqual(stylesheetViolations('@keyframes fade { from { opacity: 0; } to { opacity: 1; } } .hero { background-position: center; }'), []);
  assert.deepEqual(rules(stylesheetViolations('.skeleton-row { height: 1rem; }')), ['skeleton']);
  assert.deepEqual(rules(stylesheetViolations('.placeholder { background: var(--cp-skeleton-base); }')), ['skeleton']);
  assert.deepEqual(stylesheetViolations('.placeholder { background: var(--cp-surface-subtle); } .bone-in { margin: 0; }'), []);
});

test('loading: no visible sentence stands in for something on its way (AU.7)', () => {
  const visible = [
    '<p class="cp-muted" role="status">Loading your tags…</p>',
    '<cp-card><p class="plain" role="status">Reading your day…</p></cp-card>',
    '<p role="status">Checking this recipe…</p>',
    '<span>Finding your recipes…</span>',
    '<p class="cp-muted">Picking up where you left off…</p>',
    // A string in an interpolation or a binding is on screen too.
    '<p class="placeholder">{{ ready() ? "" : "Loading the library…" }}</p>',
    `<input [placeholder]="found ? 'Start typing…' : 'Loading labels…'" />`,
    // An apostrophe or an interpolation inside the sentence does not hide it.
    "<p class=\"cp-muted\">Loading the cook's notes…</p>",
    '<p>Loading {{ count() }} recipes…</p>',
    `<p>{{ x ? "Loading what you've made…" : '' }}</p>`,
    // A class that only ends in the hidden-status name is not it.
    '<p class="not-cp-sr-only" role="status">Loading the plan…</p>',
  ];
  for (const html of visible) assert.deepEqual(rules(templateViolations(html)), ['loading'], html);

  // A bare "Loading…" on a button, and "Loading more…" anywhere but its button.
  assert.deepEqual(rules(templateViolations(`<button cpButton type="button">{{ busy() ? 'Loading…' : 'Load older versions' }}</button>`)), ['loading']);
  assert.deepEqual(rules(templateViolations('<p class="cp-muted">Loading more…</p>')), ['loading']);
  assert.equal(templateViolations('<div>\n  <p>Reading your board…</p>\n</div>')[0].line, 2);
});

test('loading: the one hidden status, a pager saying "Loading more…" and a button saying what it is doing are fine', () => {
  const fine = [
    `<p class="cp-sr-only" role="status" aria-live="polite">{{ loading() ? 'Loading your week…' : '' }}</p>`,
    '<span class="cp-sr-only loading-status" role="status">Reading the text…</span>',
    `<button cpButton type="button">{{ loadingMore() ? 'Loading more…' : 'Load more' }}</button>`,
    `<a cpButton variant="secondary">{{ reloading() ? 'Loading the latest…' : 'Load the latest' }}</a>`,
    `<button cpButton type="button" [attr.aria-busy]="retrying()">{{ retrying() ? 'Checking…' : 'Check again' }}</button>`,
    // What the creator has just done is not something on its way.
    '<p class="cp-muted" role="status">Saving your words…</p><p role="status">Asking for your posts…</p>',
    // A sentence that is not a stand-in, a sentence bound from code, and a comment.
    '<p>Reading it uses a little of your AI allowance.</p><p>{{ copy.listsLoading }}</p><!-- <p>Loading…</p> -->',
    // Inside a closed element and back out again: the status is the sr-only one, not its sibling's.
    `<p class="cp-muted">Ready.</p><p class="cp-sr-only" role="status">{{ x ? 'Loading the plan…' : '' }}</p>`,
    // At any depth inside the hidden status, or inside the button: a span within either is still theirs.
    `<p class="cp-sr-only" role="status"><span>{{ x ? 'Loading the plan…' : '' }}</span></p>`,
    '<div class="cp-sr-only"><p role="status">Loading the plan…</p></div>',
    '<button cpButton type="button"><span>Loading more…</span></button>',
    `<p class="loading-status cp-sr-only" role="status">{{ x ? "Loading what you've made…" : '' }}</p>`,
  ];
  for (const html of fine) assert.deepEqual(templateViolations(html), [], html);
});

test('drawer: a feature positions nothing against the viewport', () => {
  assert.deepEqual(rules(stylesheetViolations('.panel { position: fixed; transform: translateX(100%); }')), ['drawer', 'drawer']);
  assert.deepEqual(stylesheetViolations('.bar { position: sticky; top: 0; }'), []);
});

test('utilities: the global utilities are neither redeclared nor kept under a private name', () => {
  for (const name of ['cp-muted', 'cp-actions', 'cp-disclosure', 'muted', 'actions']) {
    assert.deepEqual(rules(stylesheetViolations(`.card .${name} { margin: 0; }`)), ['utilities'], name);
  }
  assert.deepEqual(stylesheetViolations('.row-actions, .actions-note, .muted-count { margin: 0; }'), []);
});

test('scss: a nested rule is judged under the selector Sass writes for it', () => {
  assert.deepEqual(rules(stylesheetViolations('.card {\n  .chip { cursor: pointer; }\n}')), ['chip']);
  assert.deepEqual(rules(stylesheetViolations('.card { &.facts { margin: 0; } }')), ['facts']);
  assert.deepEqual(rules(stylesheetViolations('.list, .grid-note { & > .muted { margin: 0; } }')), ['utilities']);
  assert.deepEqual(rules(stylesheetViolations('.board { margin: 0; &-card-title { margin: 0; } }')), ['board']);
  assert.equal(stylesheetViolations('.card {\n  color: red;\n  .chip { cursor: pointer; }\n}')[0].line, 3);
});

test('scss: a nested rule does not hide its parent\'s own declarations', () => {
  assert.deepEqual(rules(frameViolations(':host {\n  display: block;\n  .inner { margin: 0; }\n  padding: 1rem;\n}')), ['frame']);
  assert.equal(frameViolations(':host {\n  display: block;\n  .inner { margin: 0; }\n  padding: 1rem;\n}')[0].line, 4);
  assert.deepEqual(rules(frameViolations(':host { @media (min-width: 40rem) { padding: 1rem; } }')), ['frame']);
  assert.deepEqual(frameViolations(':host { display: block; .inner { padding: 1rem; } }'), []);
  assert.deepEqual(rules(stylesheetViolations('.panel { position: fixed; .x { margin: 0; } }')), ['drawer']);
});

test('scss: a line comment is not a rule, and a `//` in a url or a string is not a comment', () => {
  assert.deepEqual(stylesheetViolations('// .chip { cursor: pointer; }\n.card { margin: 0; } // .muted'), []);
  assert.deepEqual(rules(stylesheetViolations('.a { background: url(https://x.test/a.png); } .chip { margin: 0; }')), ['chip']);
  assert.deepEqual(rules(stylesheetViolations(".a::before { content: '//'; } .chip { margin: 0; }")), ['chip']);
  assert.deepEqual(rules(stylesheetViolations('@use "sass:math";\n.card-#{$x} { margin: 0; } .chip { margin: 0; }')), ['chip']);
});

test('inline styles and templates are read from a component file, at their own lines', () => {
  const parts = inlineParts("@Component({\n  template: `\n    <h1>Hi</h1>\n  `,\n  styles: [\n    `\n      .muted { margin: 0; }\n    `,\n  ],\n})");
  assert.deepEqual(rules(templateViolations(parts.template.text)), ['one-h1']);
  assert.equal(parts.template.line - 1 + templateViolations(parts.template.text)[0].line, 3);
  assert.deepEqual(rules(stylesheetViolations(parts.styles.text)), ['utilities']);
});

test('an exemption excuses one rule in one file, and one that excuses nothing is reported', () => {
  const failures = [
    { file: 'src/app/a.scss', rule: 'facts', line: 3 },
    { file: 'src/app/a.scss', rule: 'chip', line: 9 },
    { file: 'src/app/b.scss', rule: 'facts', line: 1 },
  ];
  const exemptions = [
    { file: 'src/app/a.scss', rule: 'facts', reason: 'prints' },
    { file: 'src/app/gone.scss', rule: 'drawer', reason: 'was fixed since' },
  ];
  const { standing, stale } = judge(failures, exemptions);
  assert.deepEqual(standing.map((f) => `${f.file} ${f.rule}`), ['src/app/a.scss chip', 'src/app/b.scss facts']);
  assert.deepEqual(stale.map((e) => e.file), ['src/app/gone.scss']);
  assert.deepEqual(judge(failures, []).standing, failures);
});

test('rules nested in @media are read, and a stylesheet with none is quiet', () => {
  assert.deepEqual(rules(stylesheetViolations('@media (max-width: 40rem) {\n  .facts { margin: 0; }\n}')), ['facts']);
  assert.equal(stylesheetViolations('@media print {\n  .a { color: red; }\n  .muted { margin: 0; }\n}')[0].line, 3);
  assert.deepEqual(stylesheetViolations(''), []);
  assert.deepEqual(inlineParts("@Component({ templateUrl: './x.html' })"), {});
});

test('public-frame: a section is framed from the public tokens, and its host is not', () => {
  const section = (width, padding) => `.section { max-width: ${width}; margin: 0 auto; padding: ${padding}; }`;
  for (const width of ['var(--cp-public-width)', 'var(--cp-public-width-narrow)', 'var(--cp-public-width-wide)', 'var(--cp-measure-prose)']) {
    assert.deepEqual(publicStylesheetViolations(section(width, 'var(--cp-public-section-space) var(--cp-public-gutter)')), [], width);
  }
  assert.deepEqual(rules(publicStylesheetViolations(section('72rem', 'var(--cp-public-section-space) var(--cp-public-gutter)'))), ['public-frame']);
  assert.deepEqual(rules(publicStylesheetViolations(section('var(--cp-page-width-standard)', '0 var(--cp-public-gutter)'))), ['public-frame']);
  assert.deepEqual(rules(publicStylesheetViolations(section('var(--cp-public-width)', 'var(--cp-space-10) var(--cp-space-6)'))), ['public-frame']);
  assert.deepEqual(rules(publicStylesheetViolations(section('var(--cp-public-width)', '1rem 2rem 1rem var(--cp-public-gutter)'))), ['public-frame']);
  assert.deepEqual(rules(publicStylesheetViolations('.s { max-width: var(--cp-public-width); padding-inline: 1.5rem; }')), ['public-frame']);
  assert.deepEqual(rules(publicStylesheetViolations(':host { display: block; padding: 1rem; }')), ['public-frame']);
});

test('public-frame: a measure inside a section is a token; only the hero sizes its own parts', () => {
  assert.deepEqual(rules(publicStylesheetViolations('.intro { max-width: 42rem; margin: 0 auto; }')), ['public-frame']);
  assert.deepEqual(rules(publicStylesheetViolations('.intro { max-inline-size: min(100%, 30ch); }')), ['public-frame']);
  assert.deepEqual(publicStylesheetViolations('.intro { max-width: var(--cp-measure-prose); } .t { max-width: 100%; } .n { max-width: none; }'), []);
  assert.deepEqual(publicStylesheetViolations('.subheadline { max-width: 36rem; } :host { max-width: 34rem; margin-inline: auto; }', { hero: true }), []);
  // The hero still frames itself from the tokens: it draws its parts, not its own gutter.
  const literalHero = '.hero { max-width: 76rem; margin: 0 auto; padding: 1rem 1.5rem 2rem; }';
  assert.deepEqual(rules(publicStylesheetViolations(literalHero, { hero: true })), ['public-frame', 'public-frame']);
});

test('public-h1: one per public page, in the template that owns it', () => {
  assert.deepEqual(publicTemplateViolations('<section><h1>Headline</h1></section>', { headline: true }), []);
  assert.deepEqual(rules(publicTemplateViolations('<section><h2>No headline</h2></section>', { headline: true })), ['public-h1']);
  assert.deepEqual(rules(publicTemplateViolations('<h1>One</h1><h1>Two</h1>', { headline: true })), ['public-h1']);
  assert.deepEqual(rules(publicTemplateViolations('<section>\n<h1 id="x">A section</h1></section>')), ['public-h1']);
  assert.deepEqual(publicTemplateViolations('<h2>Pricing</h2><!-- <h1>old</h1> -->'), []);
});

test("public-control: a public page's buttons are the library's", () => {
  assert.deepEqual(rules(publicTemplateViolations('<button type="button" class="pause">Pause</button>')), ['public-control']);
  assert.deepEqual(publicTemplateViolations('<button type="button" cpButton variant="text">Compare</button><a cpButton routerLink="/sign-up">Start</a>'), []);
  assert.deepEqual(rules(publicTemplateViolations('<button type="button" cpButtonish>x</button>')), ['public-control']);
});

test('every exemption names a real rule and says why', () => {
  for (const exemption of EXEMPTIONS) {
    assert.ok(exemption.rule in RULES, `${exemption.file}: unknown rule ${exemption.rule}`);
    assert.ok(exemption.reason.trim().length > 20, `${exemption.file} [${exemption.rule}] needs a reason`);
    assert.match(exemption.file, /^src\/app\/[^\\]+$/, `${exemption.file} is relative to src/web, with forward slashes`);
  }
});
