// The safety-copy check's own tests: it fails on a claim, passes the subject matter, and cannot rot.
//
// A check nobody has watched fail is a check nobody should trust. These run before it in `npm test`, for the
// same reason `check-page-contract.test.mjs` runs before that one.

import { strict as assert } from 'node:assert';
import test from 'node:test';

import { DENIALS, FORBIDDEN, WATCHED, offendersIn, staleDenials } from './check-safety-copy.mjs';

const TEMPLATE = 'src/app/features/recipes/recipe-dietary-allergen.component.html';
const COMPONENT = 'src/app/features/recipes/recipe-dietary-allergen.component.ts';

test('a claim written into a template is caught', () => {
  const offenders = offendersIn('<p>This recipe is dairy-free.</p>', TEMPLATE);

  assert.equal(offenders.length, 1);
  assert.match(offenders[0], /dairy-free/);
});

test('a claim written into a component’s copy is caught', () => {
  const offenders = offendersIn("export const COPY = { ok: 'Safe to serve to children.' };", COMPONENT);

  assert.equal(offenders.length, 1);
  assert.match(offenders[0], /Safe to serve/i);
});

test('every forbidden pattern is reachable', () => {
  // Each pattern gets one sentence that should trip it, so a pattern that can never match anything — a typo,
  // an anchor in the wrong place — is found here rather than by being quietly useless forever.
  const samples = [
    'This dish is safe.',
    'It is perfectly safe.',
    'Safe to eat raw.',
    'A gluten-free loaf.',
    'Hypoallergenic.',
    'A certified recipe.',
    'Guaranteed to keep.',
    'Suitable for coeliacs.',
    'Nutrition facts below.',
    'Verified by us.',
  ];

  assert.equal(samples.length, FORBIDDEN.length);

  samples.forEach((sample, index) => {
    const [pattern] = FORBIDDEN[index];
    assert.ok(pattern.test(sample), `pattern ${index} (${pattern}) matched nothing in "${sample}"`);
  });
});

test('the subject matter of these screens is not a claim', () => {
  // The reason this check is narrow. Every one of these is a thing a creator or a record legitimately says,
  // and a check that flagged them would be switched off within a week.
  const allowed = [
    '<p>Vegan</p>',
    '<p>Excludes all animal-derived ingredients.</p>',
    '<p>Ferment for three days.</p>',
    '<p>Canning</p>',
    '<p>Medium-rare</p>',
    '<p>Raw or undercooked protein</p>',
    '<p>Gluten-Free</p>'.replace('Gluten-Free', 'Wheat'),
    '<p>Not listed in records</p>',
    '<p>Low confidence</p>',
  ];

  for (const sample of allowed) {
    assert.deepEqual(offendersIn(sample, TEMPLATE), [], `flagged: ${sample}`);
  }
});

test('an interpolation is the component’s business, not the template’s', () => {
  // The claim would be caught in the component, where the sentence actually lives; judging it twice would
  // report one problem as two and make the component's own guard look redundant.
  assert.deepEqual(offendersIn('<p>{{ copy.somethingCertified }}</p>', TEMPLATE), []);
});

test('a comment explaining the rule is not a breach of it', () => {
  const source = `
    // A screen may never say a dish is dairy-free or safe to eat.
    /* Nor certified, nor guaranteed. */
    export const COPY = { fine: 'What the records say.' };
  `;

  assert.deepEqual(offendersIn(source, COMPONENT), []);
});

test('a denial that no longer appears anywhere is reported', () => {
  const stale = staleDenials([{ path: COMPONENT, text: "const a = 'nothing like it';" }]);

  assert.deepEqual(stale, DENIALS);
});

/**
 * Both halves of every watched screen, named rather than counted: a panel whose template was watched and
 * whose component was not would be half checked, and a count only catches that while the number is right.
 */
test('it watches both halves of each watched screen', () => {
  const stems = (suffix) =>
    WATCHED.filter((path) => path.endsWith(suffix))
      .map((path) => path.slice(0, -suffix.length))
      .sort();

  assert.deepEqual(stems('.html'), stems('.ts'));
  assert.ok(stems('.ts').length >= 3);
});
