// Fails when a safety-facing screen's own words claim more than its records support.
//
// Three panels tell a creator what reviewed records say about allergens, what a nutrition estimate adds up
// to, and which high-risk methods a recipe's text names. None of them may assert that a dish is safe, free of
// an allergen, certified, suitable for a person, or that a figure is a label value — the same rule the domain
// holds for the sentences it composes (baselines B-36 and B-37), on this side of the wire.
//
// Why a script and not a spec. Each panel's spec already guards its TypeScript copy constants, which is where
// its sentences belong and where a reviewer looks. But a template can hold a literal too, and 14.7's audit
// found several — "Read against version", "at least", a column name — none of them a claim, but nothing was
// checking. Karma runs in a browser and cannot read a template from disk, so this runs beside
// check-page-contract.
//
// Deliberately narrow. It matches assurances, never subjects: "vegan", "canning", "fermentation" and
// "medium-rare" are what these screens are *about*, and a check that flagged them would be turned off within
// a week. A denial is allowed where it is unmistakably one, and each allowance is listed below by hand rather
// than inferred, because a denial and a claim differ by one word.

import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');

/**
 * The three safety panels: each one's template, and each one's copy constants.
 *
 * Both halves, because the sentences live in both. A panel's own words are declared in `PANEL_COPY` and the
 * labels beside it, and its template holds the short literals between them — a column name, a term in a facts
 * list, a word on a button. The specs guard the first half; nothing guarded the second.
 */
export const WATCHED = [
  // The landing page's FAQ answers the one question on a public page a reader's health could turn on. It is
  // answered with a refusal, and a refusal is one edit away from an assurance, so it is watched like a panel.
  'src/app/features/landing/landing-faq.component.html',
  'src/app/features/landing/landing-faq.component.ts',
  'src/app/features/recipes/recipe-dietary-allergen.component.html',
  'src/app/features/recipes/recipe-dietary-allergen.component.ts',
  'src/app/features/recipes/recipe-nutrition.component.html',
  'src/app/features/recipes/recipe-nutrition.component.ts',
  'src/app/features/recipes/recipe-food-safety.component.html',
  'src/app/features/recipes/recipe-food-safety.component.ts',
];

/** What may never be asserted, and why it would matter. */
export const FORBIDDEN = [
  [/\bis safe\b/i, 'a screen may not say a recipe or a dish is safe'],
  [/\bperfectly safe\b|\b100% safe\b|\bfood[- ]safe\b/i, 'an assurance about eating it'],
  [/\bsafe (?:to eat|to serve|for)\b/i, 'an assurance about eating it'],
  [/\b(?:gluten|dairy|nut|peanut|egg|soy|lactose|wheat|sesame|shellfish|allergen|allergy)[- ]free\b/i,
    'an absence claim the records cannot support'],
  [/\bhypoallergenic\b|\ballergen[- ](?:safe|friendly)\b/i, 'an absence claim the records cannot support'],
  [/\bcertified?\b/i, 'nothing here is certified'],
  [/\bguarantee[ds]?\b/i, 'nothing here is guaranteed'],
  [/\bsuitable for\b/i, 'suitability for a person is not something these records state'],
  [/\bnutrition facts\b|\bdaily value\b|%\s*dv\b/i, 'an estimate is not a label'],
  [/\bverified\b/i, 'nothing on these screens has been verified'],
];

/**
 * The exact phrases allowed through, each because it denies the thing it names.
 *
 * Listed by hand. A regex clever enough to tell "it is not a guarantee that a dish is free of any allergen"
 * from a claim is a regex nobody can check by reading, and these sentences are the most important ones on
 * their screens — the server's own disclaimer among them.
 */
export const DENIALS = [
  // The FAQ's allergen answer. "Never a guarantee" is the denial itself, so the word has to be allowed here
  // and nowhere else in that file.
  "they're never a guarantee",
  'it is not a clearance',
  'not a statement about what these lines contain',
  'not a statement that these lines suit anyone',
  'is not a food label',
  'that is not a statement that the recipe is safe',
  'it reads the recipe, not the food',
];

/** Interpolations are the component's business; this script judges the words a template writes itself. */
function templateText(template) {
  return template
    .replace(/<!--[\s\S]*?-->/g, ' ')
    .replace(/\{\{[\s\S]*?\}\}/g, ' ')
    .replace(/\[[\w.$-]+\]="[^"]*"/g, ' ')
    .replace(/\([\w.$-]+\)="[^"]*"/g, ' ')
    .replace(/@(?:if|else|for|switch|case|default|let)[^{]*\{?/g, ' ');
}

/**
 * A component's string literals, with its comments removed.
 *
 * The comments have to go: these files explain at length *why* they may not say a dish is free of an allergen,
 * and a scan over the prose would fail on the remarks that state the rule. What is left is what the panel
 * actually writes on screen.
 */
function componentText(source) {
  const withoutComments = source
    .replace(/\/\*[\s\S]*?\*\//g, ' ')
    .replace(/\/\/[^\n]*/g, ' ');

  const literals = withoutComments.match(/'(?:[^'\\\n]|\\.)*'|"(?:[^"\\\n]|\\.)*"|`(?:[^`\\]|\\.)*`/g) ?? [];

  return literals.join(' ');
}

function copyIn(path, text) {
  return path.endsWith('.html') ? templateText(text) : componentText(text);
}

/** Case and apostrophe style are presentation; a denial is the same sentence either way. */
function variants(denial) {
  return [denial, denial.replace(/'/g, '’')];
}

function withoutDenials(text) {
  let remaining = text;

  for (const denial of DENIALS) {
    for (const variant of variants(denial)) {
      const escaped = variant.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
      remaining = remaining.replace(new RegExp(escaped, 'gi'), ' ');
    }
  }

  return remaining;
}

export function offendersIn(source, path) {
  const text = withoutDenials(copyIn(path, source).replace(/\s+/g, ' '));
  const found = [];

  for (const [pattern, why] of FORBIDDEN) {
    const match = pattern.exec(text);

    if (match) {
      found.push(`${path}: "${match[0]}" — ${why}`);
    }
  }

  return found;
}

/** Every listed denial is still present in some watched file, so the list cannot outlive its sentences. */
export function staleDenials(watched) {
  const all = watched.map(({ path, text }) => copyIn(path, text).replace(/\s+/g, ' ')).join(' ');

  const haystack = all.toLowerCase();

  return DENIALS.filter(
    (denial) => !variants(denial).some((variant) => haystack.includes(variant.toLowerCase())),
  );
}

export function check(readFile = (path) => readFileSync(resolve(root, path), 'utf8')) {
  const watched = WATCHED.map((path) => ({ path, text: readFile(path) }));
  const offenders = watched.flatMap(({ path, text }) => offendersIn(text, path));
  const stale = staleDenials(watched);

  return {
    offenders,
    stale: stale.map((denial) => `a listed denial appears in no watched file: "${denial}"`),
  };
}

if (import.meta.url === `file://${process.argv[1]}`) {
  const { offenders, stale } = check();
  const problems = [...offenders, ...stale];

  if (problems.length > 0) {
    console.error('check-safety-copy: a safety screen claims more than its records support.\n');
    for (const problem of problems) {
      console.error(`  ${problem}`);
    }
    console.error(
      '\nSay what the records say, or state the limit as a denial and list that sentence in DENIALS.',
    );
    process.exit(1);
  }

  console.error(
    `check-safety-copy: ${WATCHED.length} files across ${WATCHED.length / 2} safety screens assert nothing their records cannot ` +
      `support (${DENIALS.length} denials listed).`,
  );
}
