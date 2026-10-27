import { Type } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRouteSnapshot, Route, Router, Routes, provideRouter } from '@angular/router';

import { routes } from '../../app.routes';
import brandSetupShellTemplate from '../brand/setup/brand-setup-shell.component.html' with { loader: 'text' };
import { BrandSetupShellComponent } from '../brand/setup/brand-setup-shell.component';
import { BrandSetupStepPlaceholderComponent } from '../brand/setup/brand-setup-step-placeholder.component';
import contentPipelineShellTemplate from '../content-pipeline/content-pipeline-shell.component.html' with {
  loader: 'text',
};
import { ContentPipelineShellComponent } from '../content-pipeline/content-pipeline-shell.component';
import { ContentPipelineStepPlaceholderComponent } from '../content-pipeline/content-pipeline-step-placeholder.component';
import { DEFAULT_VALUE_PROPS } from './landing-value-props.component';

/**
 * LND-001: every capability the landing page claims is a page the product has today.
 *
 * Each value-prop card, by heading, maps to the signed-in pages that back its words, relative to a workspace.
 * A card with no entry fails, so a new claim cannot reach the page without naming where it lives. Only a card
 * badged "In the works" is exempt, and today there is none.
 *
 * Each page is navigated to through the app's own route tree with the top-level auth guards taken off — this is
 * about what exists, not who may see it. It must resolve without a redirect to a component that is not a
 * placeholder. A step shell is one component for every step, so for those the step must also be one the shell's
 * `@switch (step.slug)` names in a `@case`: a step that falls through to its `@default` placeholder is not
 * built, however many imports say otherwise.
 */
const CAPABILITY_ROUTES: Readonly<Record<string, readonly string[]>> = {
  'Your recipe, in your words': ['recipes/new', 'recipes/recipe-1'],
  'From an idea to a first draft': ['ai-recipe-studio', 'ai-recipe-studio/draft'],
  'Food pictures on demand': ['image-studio', 'dam'],
  'A post for every channel': ['social-studio'],
  'Blog posts and newsletters': ['workflows/long-form'],
  'Sounds like you': [
    'brand/setup/goals',
    'brand/setup/style',
    'brand/setup/examples',
    'brand/style-guide/guide-1/test-drive',
  ],
  'See what’s next': ['content-board', 'my-day', 'my-week'],
  'Take it anywhere': ['recipes/recipe-1'],
  'Schedule to your social channels': ['content-board/card-1'],
};

const IN_THE_WORKS = 'In the works';
const WORKSPACE = 'sample-kitchen';

const PLACEHOLDERS: readonly Type<unknown>[] = [
  BrandSetupStepPlaceholderComponent,
  ContentPipelineStepPlaceholderComponent,
];

/** The step shells, each with the template whose step switch decides what a step renders. */
const STEP_SHELLS = new Map<Type<unknown>, { template: string; placeholder: string }>([
  [BrandSetupShellComponent, { template: brandSetupShellTemplate, placeholder: 'cp-brand-setup-step-placeholder' }],
  [
    ContentPipelineShellComponent,
    { template: contentPipelineShellTemplate, placeholder: 'cp-content-pipeline-step-placeholder' },
  ],
]);

/** The text between the brace at `open` and the one that closes it. */
function blockAt(text: string, open: number): string {
  let depth = 0;
  for (let i = open; i < text.length; i += 1) {
    if (text[i] === '{') depth += 1;
    if (text[i] === '}' && --depth === 0) return text.slice(open + 1, i);
  }
  throw new Error('unbalanced braces in template');
}

/** The `@case` slugs of a template's step switch, and the body of its `@default`. */
function stepSwitch(template: string): { cases: string[]; fallback: string } {
  const start = template.indexOf('@switch (step.slug)');
  if (start < 0) throw new Error('no @switch (step.slug) in the shell template');
  const body = blockAt(template, template.indexOf('{', start));

  const cases: string[] = [];
  let fallback = '';
  for (let i = 0; i < body.length; i += 1) {
    if (body[i] === '{') {
      i += blockAt(body, i).length + 1;
    } else if (body.startsWith('@case', i)) {
      const slug = /^@case \('([^']+)'\)/.exec(body.slice(i))?.[1];
      if (slug) cases.push(slug);
    } else if (body.startsWith('@default', i)) {
      fallback = blockAt(body, body.indexOf('{', i));
    }
  }
  return { cases, fallback };
}

function withoutTopLevelGuards(tree: Routes): Routes {
  return tree.map(({ canActivate: _guards, ...route }) => route);
}

function leafOf(snapshot: ActivatedRouteSnapshot): ActivatedRouteSnapshot {
  let leaf = snapshot;
  while (leaf.firstChild) leaf = leaf.firstChild;
  return leaf;
}

async function componentOf(leaf: ActivatedRouteSnapshot): Promise<Type<unknown> | null> {
  if (leaf.component) return leaf.component;
  const config: Route | null = leaf.routeConfig;
  if (!config?.loadComponent) return null;
  const loaded = await config.loadComponent();
  return ('default' in loaded ? loaded.default : loaded) as Type<unknown>;
}

describe('Landing capability routes (LND-001)', () => {
  let router: Router;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideRouter(withoutTopLevelGuards(routes))] });
    router = TestBed.inject(Router);
  });

  it('maps every card not badged "In the works" to at least one page', () => {
    const unmapped = DEFAULT_VALUE_PROPS.filter(
      (item) => item.badgeLabel !== IN_THE_WORKS && !CAPABILITY_ROUTES[item.heading]?.length,
    ).map((item) => item.heading);
    expect(unmapped).withContext('cards with no backing page').toEqual([]);
  });

  it('maps no card the page no longer shows, so the table cannot drift into vouching for nothing', () => {
    const headings = new Set(DEFAULT_VALUE_PROPS.map((item) => item.heading));
    expect(Object.keys(CAPABILITY_ROUTES).filter((heading) => !headings.has(heading))).toEqual([]);
  });

  it('knows where each step shell falls through to its placeholder', () => {
    for (const [shell, { template, placeholder }] of STEP_SHELLS) {
      const { cases, fallback } = stepSwitch(template);
      expect(cases.length).withContext(shell.name).toBeGreaterThan(0);
      expect(fallback).withContext(`${shell.name} @default`).toContain(placeholder);
    }
  });

  for (const item of DEFAULT_VALUE_PROPS.filter((each) => each.badgeLabel !== IN_THE_WORKS)) {
    for (const path of CAPABILITY_ROUTES[item.heading] ?? []) {
      it(`backs "${item.heading}" with a real page at ${path}`, async () => {
        const url = `/${WORKSPACE}/${path}`;

        expect(await router.navigateByUrl(url))
          .withContext(`navigating to ${url}`)
          .toBeTrue();
        expect(router.url).withContext('redirected away from the claimed page').toBe(url);

        const leaf = leafOf(router.routerState.snapshot.root);
        const component = await componentOf(leaf);
        expect(component).withContext(`no component at ${url}`).not.toBeNull();
        expect(PLACEHOLDERS).withContext(`${url} is a placeholder`).not.toContain(component!);

        const step = leaf.paramMap.get('step');
        if (step !== null) {
          const shell = STEP_SHELLS.get(component!);
          expect(shell).withContext(`${component!.name} takes a step but is not a known step shell`).toBeDefined();
          expect(stepSwitch(shell!.template).cases)
            .withContext(`step "${step}" falls through to the placeholder`)
            .toContain(step);
        }
      });
    }
  }
});
