import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';

import { WorkspaceRole } from '../../models/auth.models';
import { ContentPipelineDraft, emptyContentPipelineDraft } from '../../models/content-pipeline.models';
import { LongFormDraftStatus, LongFormDraftSummary } from '../../models/long-form-draft.models';
import { WorkflowDefinitionSummary, WorkflowRunSummary, WorkflowRunSummaryPage } from '../../models/workflow-hub.models';
import { decodeWorkflowRun } from '../../models/workflow-run.models';
import { wireRun } from '../../models/workflow-run.testing';
import {
  ContentPipelineDraftOwner,
  ContentPipelineDraftService,
  ContentPipelineReadResult,
} from '../../services/content-pipeline-draft.service';
import { LongFormDraftPageOutcome, LongFormDraftService } from '../../services/long-form-draft.service';
import { WorkflowHubService, WorkflowRunListOutcome, WorkflowRunListView } from '../../services/workflow-hub.service';
import { WorkflowRunRestartOutcome, WorkflowRunService } from '../../services/workflow-run.service';
import { MyMembershipsState, WorkspaceMembershipService } from '../../services/workspace-membership.service';
import { LONG_FORM_DEFINITION_KEY, WorkflowsHubComponent } from './workflows-hub.component';

@Component({ selector: 'cp-test-elsewhere', template: '<p>elsewhere</p>' })
class ElsewhereComponent {}

const SLUG = 'cozy-fall';
const OTHER_SLUG = 'other-kitchen';
const ownerKey = (owner: ContentPipelineDraftOwner): string => `${owner.workspaceId}.${owner.membershipId}`;
const delay = (ms: number): Promise<void> => new Promise((resolve) => setTimeout(resolve, ms));

function member(workspaceId: string, slug: string, membershipId: string, role: WorkspaceRole) {
  return { workspaceId, workspaceSlug: slug, workspaceName: slug, membershipId, role, status: 'Active' as const };
}

function definition(key: string, goal: string, overrides: Partial<WorkflowDefinitionSummary> = {}): WorkflowDefinitionSummary {
  return {
    key,
    goal,
    description: `${goal}, one step at a time.`,
    expectedOutcome: `Something you made by choosing to ${goal.toLowerCase()}.`,
    available: true,
    canStart: true,
    neededRole: null,
    waitingFor: [],
    ...overrides,
  };
}

const DEFINITIONS: readonly WorkflowDefinitionSummary[] = [
  definition('recipe.create', 'Create a recipe'),
  definition('brand.voice', 'Create my brand voice', { canStart: false, neededRole: 'Editor' }),
  definition(LONG_FORM_DEFINITION_KEY, 'Write a blog post or newsletter'),
  definition('publishing.plan-and-publish', 'Plan and publish content', {
    canStart: false,
    waitingFor: [{ message: 'Set your workspace’s time zone in brand settings first.', route: 'brand' }],
  }),
  definition('planning.day-and-week', 'Plan my day and week', { available: false, canStart: false }),
];

function summary(id: string, overrides: Partial<WorkflowRunSummary> = {}): WorkflowRunSummary {
  return {
    id,
    title: 'Create a recipe',
    status: 'InProgress',
    definitionKey: 'recipe.create',
    goal: 'Create a recipe',
    owner: { membershipId: 'm1', displayName: 'Sam' },
    stepCount: 5,
    completedStepCount: 1,
    skippedStepCount: 0,
    currentStepTitle: 'Ingredients',
    nextStepTitle: 'Ingredients',
    updatedAt: '2026-10-14T10:00:00Z',
    completedAt: null,
    attentionReasons: [],
    ...overrides,
  };
}

function piece(id: string, status: LongFormDraftStatus, updatedAt: string): LongFormDraftSummary {
  return { id, creativeContextId: `ctx-${id}`, kind: 'BlogArticle', status, latestRevisionNumber: 1, updatedAt };
}

/** A draft that has reached the picture step. */
function inProgress(savedAt = '2026-10-13T09:00:00Z'): ContentPipelineDraft {
  const base = emptyContentPipelineDraft(new Date(savedAt));
  return { ...base, config: { ...base.config, concept: 'A tight crop.' }, furthestStep: 'picture' };
}

// ---- The "server" ----

let definitions: Record<string, readonly WorkflowDefinitionSummary[] | 'unavailable'>;
let runPages: Record<string, Partial<Record<WorkflowRunListView, WorkflowRunSummaryPage | 'unavailable'>>>;
let runReads: { slug: string; view: WorkflowRunListView; cursor: string | null }[];
let failNextPage: boolean;
/** Set to hold the next definitions read until it is called. */
let holdDefinitions: (() => void) | null | undefined;
let definitionReads: string[];
let longForm: Record<string, readonly LongFormDraftSummary[]>;
let longFormOutcome: LongFormDraftPageOutcome['status'] | null;
let stored: Record<string, ContentPipelineDraft | null>;
let unreadable: Set<string>;
let start: jasmine.Spy<(slug: string, key: string, idempotencyKey: string) => Promise<WorkflowRunRestartOutcome>>;
let loads: number;
let harness: RouterTestingHarness;
let memberships: ReturnType<typeof signal<MyMembershipsState>>;

const hubStore = {
  definitions: (slug: string) => {
    definitionReads.push(slug);
    const read = () => {
      const answer = definitions[slug] ?? [];
      return answer === 'unavailable' ? { status: 'unavailable' as const } : { status: 'found' as const, definitions: answer };
    };
    if (holdDefinitions === undefined) return Promise.resolve(read());
    return new Promise<ReturnType<typeof read>>((resolve) => (holdDefinitions = () => resolve(read())));
  },
  runs: (slug: string, query: { view: WorkflowRunListView; cursor?: string | null }): Promise<WorkflowRunListOutcome> => {
    runReads.push({ slug, view: query.view, cursor: query.cursor ?? null });
    const answer = runPages[slug]?.[query.view] ?? { items: [], nextCursor: null };
    if (answer === 'unavailable') return Promise.resolve({ status: 'unavailable' });
    if (query.cursor && failNextPage) return Promise.resolve({ status: 'unavailable' });
    if (query.cursor) return Promise.resolve({ status: 'found', page: { items: [summary(`${query.view}-page-2`, { title: 'From the next page', updatedAt: '2026-09-01T00:00:00Z' })], nextCursor: null } });
    return Promise.resolve({ status: 'found', page: answer });
  },
};

const longFormStore = {
  list: (slug: string): Promise<LongFormDraftPageOutcome> =>
    longFormOutcome !== null && longFormOutcome !== 'found'
      ? Promise.resolve({ status: longFormOutcome } as LongFormDraftPageOutcome)
      : Promise.resolve({ status: 'found', page: { items: longForm[slug] ?? [], nextCursor: null } }),
};

const draftStore = {
  read: (owner: ContentPipelineDraftOwner): ContentPipelineReadResult => {
    const key = ownerKey(owner);
    return { draft: stored[key] ?? null, discarded: unreadable.delete(key) };
  },
  lastContextId: () => null,
  readKept: () => ({ kept: null, discarded: false }),
};

async function settle(): Promise<void> {
  for (let i = 0; i < 4; i += 1) {
    await delay(0);
    harness.detectChanges();
  }
}

async function create(
  options: {
    role?: WorkspaceRole;
    state?: MyMembershipsState;
    definitions?: readonly WorkflowDefinitionSummary[] | 'unavailable';
    runs?: Partial<Record<WorkflowRunListView, WorkflowRunSummaryPage | 'unavailable'>>;
    longForm?: readonly LongFormDraftSummary[];
    longFormOutcome?: LongFormDraftPageOutcome['status'];
    draft?: ContentPipelineDraft | null;
    unreadable?: boolean;
  } = {},
): Promise<void> {
  definitions = { [SLUG]: options.definitions ?? DEFINITIONS, [OTHER_SLUG]: [definition('recipe.create', 'Create a recipe')] };
  runPages = { [SLUG]: options.runs ?? {} };
  runReads = [];
  failNextPage = false;
  holdDefinitions = undefined;
  definitionReads = [];
  longForm = { [SLUG]: options.longForm ?? [] };
  longFormOutcome = options.longFormOutcome ?? null;
  stored = { 'w1.m1': options.draft ?? null };
  unreadable = new Set(options.unreadable ? ['w1.m1'] : []);
  loads = 0;
  start = jasmine.createSpy('start').and.resolveTo({ status: 'started', run: decodeWorkflowRun(wireRun({ id: 'run-new' }))! });

  const role = options.role ?? 'Editor';
  memberships = signal<MyMembershipsState>(
    options.state ?? { status: 'ready', memberships: [member('w1', SLUG, 'm1', role), member('w2', OTHER_SLUG, 'm2', role)] },
  );

  TestBed.resetTestingModule();
  await TestBed.configureTestingModule({
    providers: [
      provideRouter([
        { path: ':workspaceSlug/workflows', pathMatch: 'full', component: WorkflowsHubComponent },
        { path: '**', component: ElsewhereComponent },
      ]),
      { provide: WorkflowHubService, useValue: hubStore },
      { provide: WorkflowRunService, useValue: { start } },
      { provide: ContentPipelineDraftService, useValue: draftStore },
      { provide: LongFormDraftService, useValue: longFormStore },
      {
        provide: WorkspaceMembershipService,
        useValue: {
          state: memberships,
          ensureLoaded: () => Promise.resolve(),
          load: () => {
            loads += 1;
            return Promise.resolve();
          },
        },
      },
    ],
  }).compileComponents();

  harness = await RouterTestingHarness.create();
  await harness.navigateByUrl(`/${SLUG}/workflows`);
  await settle();
}

function root(): HTMLElement {
  return harness.routeNativeElement as HTMLElement;
}

function text(): string {
  return (root().textContent ?? '').replace(/\s+/g, ' ');
}

function card(heading: string): HTMLElement | null {
  const found = Array.from(root().querySelectorAll('cp-topic-card')).find(
    (candidate) => candidate.querySelector('h2, h3')?.textContent?.trim() === heading,
  );
  return (found as HTMLElement | undefined) ?? null;
}

function rows(heading: string): string[] {
  return Array.from(card(heading)?.querySelectorAll('.rows > li') ?? []).map((row) => (row.textContent ?? '').replace(/\s+/g, ' ').trim());
}

function goals(): string[] {
  return Array.from(root().querySelectorAll('section.goals cp-topic-card h3')).map((h) => h.textContent?.trim() ?? '');
}

function control(scope: Element | null, label: string): HTMLElement | null {
  return (Array.from(scope?.querySelectorAll<HTMLElement>('a, button') ?? []).find((el) => el.textContent?.trim() === label) ?? null);
}

async function search(value: string): Promise<void> {
  const input = root().querySelector<HTMLInputElement>('input[type="search"]')!;
  input.value = value;
  input.dispatchEvent(new Event('input'));
  await settle();
}

describe('WorkflowsHubComponent', () => {
  describe('first use', () => {
    it('leads with what the creator can do, and shows no empty sections', async () => {
      await create();

      expect(text()).toContain('Nothing in progress yet');
      // Drawn, and compact: the goals below it are the first thing to do (AU.6.3).
      const empty = root().querySelector('section.goals cp-empty-state')!;
      expect(empty.querySelector('cp-illustration')!.getAttribute('data-name')).toBe('board');
      expect(empty.getAttribute('data-variant')).toBe('no-results');
      expect(card('Continue where you left off')).toBeNull();
      expect(card('Needs attention')).toBeNull();
      expect(card('Recent outcomes')).toBeNull();
      expect(goals()).toEqual([
        'Create a recipe',
        'Create my brand voice',
        'Write a blog post or newsletter',
        'Plan and publish content',
        'Make a picture and posts from one idea',
        'Draft a recipe from a dish name',
      ]);
    });

    it('never shows a workflow that is not built, or any word of how one is built', async () => {
      await create();

      expect(text()).not.toContain('Plan my day and week');
      for (const jargon of ['recipes.new', 'capability', 'prompt', 'model', 'definition']) {
        expect(text().toLowerCase()).withContext(jargon).not.toContain(jargon);
      }
    });
  });

  describe('returning', () => {
    it('puts work in progress first, from every place it is kept, newest first', async () => {
      await create({
        runs: { mine: { items: [summary('run-1', { title: 'Soda bread', updatedAt: '2026-10-14T10:00:00Z' })], nextCursor: null } },
        longForm: [piece('lf-1', 'Writing', '2026-10-15T08:00:00Z'), piece('lf-2', 'Accepted', '2026-10-15T09:00:00Z')],
        draft: inProgress('2026-10-12T09:00:00Z'),
      });

      const shown = rows('Continue where you left off');
      expect(shown.length).toBe(3);
      expect(shown[0]).toContain('Write a blog post or newsletter');
      expect(shown[1]).toContain('Create a recipe');
      expect(shown[1]).toContain('Soda bread');
      expect(shown[1]).toContain('Next: Ingredients · 1 of 5 steps done');
      expect(shown[2]).toContain('Make a picture and posts from one idea');

      const links = Array.from(card('Continue where you left off')!.querySelectorAll('a')).map((a) => a.getAttribute('href'));
      expect(links).toEqual([
        `/${SLUG}/workflows/long-form/context/ctx-lf-1/draft/lf-1`,
        `/${SLUG}/workflows/runs/run-1`,
        `/${SLUG}/workflows/content-pipeline/picture`,
      ]);
      expect(text()).not.toContain('Nothing in progress yet');
    });

    // From AU.0: three runs nobody named all read "Create a recipe", and each Continue was the same link (AU.6.3).
    it('tells same-kind runs apart by the step each is on and when it was saved, for the eye and a screen reader', async () => {
      await create({
        runs: {
          mine: {
            items: [
              summary('run-a', { nextStepTitle: 'Check your ingredients', updatedAt: '2026-10-15T07:15:00Z' }),
              summary('run-b', { nextStepTitle: 'Start your recipe', updatedAt: '2026-10-15T06:44:00Z' }),
              summary('run-c', { nextStepTitle: 'Start your recipe', updatedAt: '2026-10-15T04:43:00Z' }),
            ],
            nextCursor: null,
          },
        },
      });

      const continuing = card('Continue where you left off')!;
      const names = Array.from(continuing.querySelectorAll('a')).map((a) => a.getAttribute('aria-label')!);
      expect(new Set(names).size).toBe(3);
      expect(names[0]).toMatch(/^Continue Create a recipe — Check your ingredients, last saved /);
      expect(names[1]).toMatch(/^Continue Create a recipe — Start your recipe, last saved /);

      // Each still begins with what it shows (WCAG 2.5.3), and each row says when it was saved.
      for (const link of Array.from(continuing.querySelectorAll('a'))) {
        expect(link.getAttribute('aria-label')!.startsWith(link.textContent!.trim())).toBeTrue();
      }
      expect(new Set(rows('Continue where you left off')).size).toBe(3);
    });

    it('names a run the creator did name by that name, and the day it was finished', async () => {
      await create({
        runs: {
          completed: {
            items: [
              summary('done-1', { status: 'Completed', completedAt: '2026-10-18T12:00:00Z', nextStepTitle: null }),
              summary('done-2', { title: 'Soda bread', status: 'Completed', completedAt: '2026-10-17T12:00:00Z', nextStepTitle: null }),
            ],
            nextCursor: null,
          },
        },
      });

      const names = Array.from(card('Recent outcomes')!.querySelectorAll('a')).map((a) => a.getAttribute('aria-label')!);
      expect(names[0]).toMatch(/^See what you made with Create a recipe — Ingredients, finished /);
      expect(names[1]).toMatch(/^See what you made with “Soda bread”, finished /);
    });

    it('says when each run that needs a look was last saved, so three alike are three rows', async () => {
      await create({
        runs: {
          attention: {
            items: [
              summary('stale-1', { attentionReasons: ['idle'], updatedAt: '2026-10-01T09:00:00Z' }),
              summary('stale-2', { attentionReasons: ['idle'], updatedAt: '2026-10-02T09:00:00Z' }),
            ],
            nextCursor: null,
          },
        },
      });

      const shown = rows('Needs attention');
      expect(shown.every((row) => row.includes('Last saved'))).toBeTrue();
      expect(new Set(shown).size).toBe(2);
      const names = Array.from(card('Needs attention')!.querySelectorAll('a')).map((a) => a.getAttribute('aria-label'));
      expect(new Set(names).size).toBe(2);
    });

    it('shows five, then more on asking — reading the next page of runs when it needs to', async () => {
      const items = Array.from({ length: 6 }, (_, i) => summary(`run-${i}`, { title: `Run ${i}`, updatedAt: `2026-10-1${i}T10:00:00Z` }));
      await create({ runs: { mine: { items, nextCursor: 'next-page' } } });

      expect(rows('Continue where you left off').length).toBe(5);

      control(card('Continue where you left off'), 'Show more')!.click();
      await settle();

      expect(rows('Continue where you left off').length).toBe(7);
      expect(runReads.some((read) => read.view === 'mine' && read.cursor === 'next-page')).toBeTrue();
      expect(control(card('Continue where you left off'), 'Show more')).toBeNull();
    });

    it('says why each thing needs a look, and whose it is when it is not the reader’s', async () => {
      await create({
        runs: {
          attention: {
            items: [
              summary('run-a', { title: 'Old run', owner: { membershipId: 'm9', displayName: 'Robin' }, attentionReasons: ['idle', 'newer_version'] }),
              summary('run-b', { title: 'Gone owner', owner: { membershipId: 'm8', displayName: null }, attentionReasons: ['not_yours_to_do'] }),
            ],
            nextCursor: null,
          },
        },
        longForm: [piece('lf-9', 'NeedsReview', '2026-10-15T08:00:00Z')],
      });

      const shown = rows('Needs attention');
      expect(shown[0]).toContain('No one has touched this in a week.');
      expect(shown[0]).toContain('A newer version of this journey is available.');
      expect(shown[0]).toContain('Started by Robin.');
      expect(shown[1]).toContain('Someone with more access needs to do the next step.');
      expect(shown[1]).toContain('Started by someone who has left the workspace.');
      expect(shown[2]).toContain('It needs your review.');
      // Status is said in words, once per row, not only by colour.
      expect(card('Needs attention')!.querySelectorAll('cp-status-pill').length).toBe(3);
    });

    it('says when the next page could not be read, and reads it again on asking', async () => {
      const items = Array.from({ length: 5 }, (_, i) => summary(`run-${i}`, { title: `Run ${i}` }));
      await create({ runs: { mine: { items, nextCursor: 'next-page' } } });
      failNextPage = true;

      control(card('Continue where you left off'), 'Show more')!.click();
      await settle();
      expect(card('Continue where you left off')!.textContent).toContain('More couldn’t be loaded right now.');

      failNextPage = false;
      control(card('Continue where you left off'), 'Try again')!.click();
      await settle();
      expect(rows('Continue where you left off').length).toBe(6);
      expect(card('Continue where you left off')!.textContent).not.toContain('More couldn’t be loaded');
    });

    it('lets every list show more than five, so nothing that needs a look is out of reach', async () => {
      const many = (view: string) => Array.from({ length: 7 }, (_, i) =>
        summary(`${view}-${i}`, { title: `${view} ${i}`, status: view === 'done' ? 'Completed' : 'InProgress', attentionReasons: ['idle'] }));
      await create({ runs: { attention: { items: many('look'), nextCursor: null }, completed: { items: many('done'), nextCursor: null } } });

      expect(rows('Needs attention').length).toBe(5);
      control(card('Needs attention'), 'Show more')!.click();
      await settle();
      expect(rows('Needs attention').length).toBe(7);

      expect(rows('Recent outcomes').length).toBe(5);
      control(card('Recent outcomes'), 'Show more')!.click();
      await settle();
      expect(rows('Recent outcomes').length).toBe(7);
    });

    it('lists what was finished recently, leading to what was made', async () => {
      await create({
        runs: { completed: { items: [summary('run-done', { title: 'Soda bread', status: 'Completed', completedAt: '2026-10-10T12:00:00Z' })], nextCursor: null } },
      });

      expect(rows('Recent outcomes')[0]).toContain('Soda bread');
      expect(rows('Recent outcomes')[0]).toContain('Finished');
      expect(control(card('Recent outcomes'), 'See what you made')!.getAttribute('href')).toBe(`/${SLUG}/workflows/runs/run-done`);
    });
  });

  describe('starting', () => {
    it('starts a workflow as a run and opens it', async () => {
      await create();

      control(card('Create a recipe'), 'Start')!.click();
      await settle();

      expect(start).toHaveBeenCalledOnceWith(SLUG, 'recipe.create', jasmine.any(String));
      expect(TestBed.inject(Router).url).toBe(`/${SLUG}/workflows/runs/run-new`);
    });

    it('retries a start that did not go through under the same key, so it never starts two', async () => {
      await create();
      start.and.resolveTo({ status: 'unavailable', offline: false });

      control(card('Create a recipe'), 'Start')!.click();
      await settle();
      expect(card('Create a recipe')!.textContent).toContain('Nothing was started — try again.');

      control(card('Create a recipe'), 'Start')!.click();
      await settle();

      const keys = start.calls.allArgs().map((args) => args[2]);
      expect(keys.length).toBe(2);
      expect(keys[0]).toBe(keys[1]);
    });

    it('says why a start was refused, and reads what can be started again', async () => {
      await create();
      start.and.resolveTo({ status: 'unprocessable', message: 'This journey needs something else first.' });
      const before = definitionReads.length;

      control(card('Create a recipe'), 'Start')!.click();
      await settle();

      expect(card('Create a recipe')!.textContent).toContain('This journey needs something else first.');
      expect(definitionReads.length).toBe(before + 1);
    });

    it('reads the cards again in place when a start is refused for the reader’s access, keeping what it said', async () => {
      await create();
      start.and.resolveTo({ status: 'forbidden' });
      const before = definitionReads.length;

      control(card('Create a recipe'), 'Start')!.click();
      await settle();

      expect(definitionReads.length).toBe(before + 1);
      expect(card('Create a recipe')!.textContent).toContain('Your access in this workspace doesn’t let you start this.');
    });

    it('says who can start what the reader cannot, and offers no Start', async () => {
      await create();

      const brand = card('Create my brand voice')!;
      expect(brand.textContent).toContain('Only an Editor or Owner of this workspace can start this.');
      expect(control(brand, 'Start')).toBeNull();
    });

    it('says what a workflow needs first, and links to where to sort it out', async () => {
      await create();

      const publish = card('Plan and publish content')!;
      expect(publish.textContent).toContain('Set your workspace’s time zone in brand settings first.');
      expect(control(publish, 'Open brand settings')!.getAttribute('href')).toBe(`/${SLUG}/brand`);
      expect(control(publish, 'Start')).toBeNull();
    });

    it('opens the long-form writer for a blog post with one Start, without starting a run', async () => {
      await create();

      // One primary action, as on every other card. It had two — from a recipe and from a brief — which opened
      // the same page and differed only in which field took focus, so the choice bought the creator nothing and
      // came before the journey had explained either. A piece starts from one recipe; the brief field is still
      // on that first step for a piece with no recipe behind it.
      const blog = card('Write a blog post or newsletter')!;
      expect(control(blog, 'Start')!.getAttribute('href')).toBe(`/${SLUG}/workflows/long-form?from=recipe`);
      expect(control(blog, 'Start from a brief')).toBeNull();
    });

    it('says before Start that the picture-and-posts journey keeps unsent wording on this device', async () => {
      await create();

      // The resume row said it, but only once a run existed — so a creator learned it part-way through, after
      // typing. The run itself is kept in the workspace; only wording not yet sent is this device's.
      const pictures = card('Make a picture and posts from one idea')!;
      expect(pictures.textContent).toContain('not yet sent stays on this device');
    });

    it('lets a Viewer see every way in and start none of them', async () => {
      await create({ role: 'Viewer', definitions: DEFINITIONS.map((d) => ({ ...d, canStart: false, neededRole: 'Contributor' as const })) });

      expect(root().querySelectorAll('section.goals [cpTopicCardActions] a, section.goals [cpTopicCardActions] button').length).toBe(0);
      expect(card('Draft a recipe from a dish name')!.textContent).toContain('You have view-only access here');
      expect(card('Continue where you left off')).toBeNull();
    });
  });

  describe('search', () => {
    it('narrows what can be done as the creator types, and says how many match', async () => {
      await create();

      await search('recipe');

      expect(goals()).toEqual(['Create a recipe', 'Draft a recipe from a dish name']);
      expect(root().querySelector('section.goals .search [role="status"]')!.textContent).toContain('2 of 6 shown.');
    });

    it('says when nothing matches, and clears the search on asking', async () => {
      await create();

      await search('spaceship');
      expect(text()).toContain('Nothing matches that');
      // The one under the search, not first use's above it.
      const noResults = Array.from(root().querySelectorAll('section.goals cp-empty-state')).find((each) =>
        each.textContent?.includes('Try another word'),
      )!;
      expect(noResults.querySelector('cp-illustration')!.getAttribute('data-name')).toBe('search');

      control(root().querySelector('section.goals'), 'Clear search')!.click();
      await settle();
      expect(goals().length).toBe(6);
    });
  });

  // ---- While it loads (AU.6.3): the page arriving, said once ----

  describe('while it loads', () => {
    const status = () => root().querySelector('.hub > p.cp-sr-only[role="status"]')!;

    it('draws a card of work in progress and three goal cards while the workspaces are read', async () => {
      await create({ state: { status: 'loading' } });

      expect(status().textContent).toBe('Loading your workflows…');
      const busy = root().querySelector('.loading[aria-busy="true"]')!;
      expect(busy.querySelectorAll('cp-card cp-skeleton').length).toBe(4);
      expect(busy.textContent!.trim()).toBe('');
      expect(root().querySelector('section.goals')).toBeNull();
    });

    it('holds the work-in-progress place with a skeleton while the runs are read', async () => {
      spyOn(hubStore, 'runs').and.returnValue(new Promise<WorkflowRunListOutcome>(() => undefined));
      await create();

      expect(status().textContent).toBe('Checking what you have in progress…');
      const place = root().querySelector('ul.topics[aria-busy="true"]')!;
      expect(place.getAttribute('aria-hidden')).toBe('true');
      expect(place.querySelectorAll('cp-skeleton').length).toBe(1);
      expect(text()).not.toContain('Nothing in progress yet');
    });

    it('says both parts still on its way when the runs and the workflows are read at once', async () => {
      await create();
      holdDefinitions = null;
      spyOn(hubStore, 'runs').and.returnValue(new Promise<WorkflowRunListOutcome>(() => undefined));
      await harness.navigateByUrl(`/${OTHER_SLUG}/workflows`);
      await settle();

      expect(status().textContent).toBe('Checking what you have in progress… Loading what you can do…');
      expect(root().querySelector('ul.topics[aria-busy="true"]')).not.toBeNull();
      expect(root().querySelectorAll('li.goal-loading').length).toBe(3);
    });

    it('lists the journeys that need no reading, then skeletons for the workflows still on their way', async () => {
      await create();
      // Hold the next read, and read the next workspace: its own workflows are then on their way.
      holdDefinitions = null;
      await harness.navigateByUrl(`/${OTHER_SLUG}/workflows`);
      await settle();

      expect(status().textContent).toBe('Loading what you can do…');
      expect(goals()).toEqual(['Make a picture and posts from one idea', 'Draft a recipe from a dish name']);
      const placeholders = root().querySelectorAll('section.goals li.goal-loading[aria-hidden="true"]');
      expect(placeholders.length).toBe(3);

      holdDefinitions!();
      await settle();
      expect(root().querySelector('li.goal-loading')).toBeNull();
      expect(status().textContent).toBe('');
    });
  });

  describe('when something cannot be read', () => {
    it('stops the page only when the creator’s workspaces cannot be read, and offers Try again', async () => {
      await create({ state: { status: 'error' } });

      expect(text()).toContain('Your workspaces couldn’t be loaded right now.');
      control(root(), 'Try again')!.click();
      expect(loads).toBe(1);
    });

    it('says an unknown workspace is not found', async () => {
      await create({ state: { status: 'ready', memberships: [member('w2', OTHER_SLUG, 'm2', 'Owner')] } });

      expect(text()).toContain('We couldn’t find this workspace.');
    });

    it('keeps the journeys with pages of their own when the workflows cannot be read, and reads them again on asking', async () => {
      await create({ definitions: 'unavailable' });

      expect(text()).toContain('Some of what you can do couldn’t be loaded right now.');
      expect(goals()).toEqual(['Make a picture and posts from one idea', 'Draft a recipe from a dish name']);

      definitions[SLUG] = DEFINITIONS;
      control(root().querySelector('section.goals'), 'Try again')!.click();
      await settle();
      expect(goals()).toContain('Create a recipe');
    });

    it('keeps Try again where it is while trying again, then takes focus to the section', async () => {
      await create({ definitions: 'unavailable' });
      holdDefinitions = null;
      definitions[SLUG] = DEFINITIONS;

      const retry = control(root().querySelector('section.goals'), 'Try again')!;
      retry.focus();
      retry.click();
      await settle();

      const trying = control(root().querySelector('section.goals'), 'Trying again…')!;
      expect(trying.getAttribute('aria-disabled')).toBe('true');
      expect(document.activeElement).toBe(trying);

      holdDefinitions!();
      await settle();
      expect(goals()).toContain('Create a recipe');
      expect(document.activeElement?.id).toBe('hub-goals-heading');
    });

    it('keeps the work-in-progress card with a notice when runs cannot be read, and reads them again on asking', async () => {
      await create({ runs: { mine: 'unavailable' }, draft: inProgress() });

      const continuing = card('Continue where you left off')!;
      expect(continuing.textContent).toContain('Some of your work in progress couldn’t be loaded right now.');
      expect(rows('Continue where you left off').length).toBe(1);

      runPages[SLUG]['mine'] = { items: [summary('run-1')], nextCursor: null };
      control(continuing, 'Try again')!.click();
      await settle();
      expect(rows('Continue where you left off').length).toBe(2);
      expect(document.activeElement?.id).toBe('hub-continue-heading');
    });

    it('says when a run kept on this device could not be read', async () => {
      await create({ unreadable: true });

      expect(text()).toContain('couldn’t be read, so it was set aside');
    });
  });

  describe('accessibility', () => {
    it('has one page heading, and headings in order beneath it', async () => {
      await create({ runs: { mine: { items: [summary('run-1')], nextCursor: null } } });

      expect(root().querySelectorAll('h1').length).toBe(1);
      const levels = Array.from(root().querySelectorAll('h1, h2, h3')).map((h) => Number(h.tagName[1]));
      levels.forEach((level, i) => {
        if (i > 0) expect(level - levels[i - 1]).withContext(`heading ${i}`).toBeLessThanOrEqual(1);
      });
      expect(root().querySelector('#hub-goals-heading')!.tagName).toBe('H2');
    });

    it('names the search, and every link and button by what it does', async () => {
      await create({ runs: { mine: { items: [summary('run-1', { title: 'Soda bread' })], nextCursor: null } } });

      expect(root().querySelector('cp-search-field')!.getAttribute('label')).toBe('Search what you can do');
      for (const el of Array.from(root().querySelectorAll<HTMLElement>('a, button'))) {
        const name = el.getAttribute('aria-label') ?? el.textContent?.trim() ?? '';
        expect(name.length).withContext(el.outerHTML).toBeGreaterThan(0);
      }
      expect(control(card('Continue where you left off'), 'Continue')!.getAttribute('aria-label')).toMatch(/^Continue “Soda bread”, last saved /);

      // A name that says more still begins with what is shown (WCAG 2.5.3).
      for (const el of Array.from(root().querySelectorAll<HTMLElement>('a[aria-label], button[aria-label]'))) {
        const shown = el.textContent?.trim() ?? '';
        expect(el.getAttribute('aria-label')!.startsWith(shown)).withContext(`${shown} / ${el.getAttribute('aria-label')}`).toBeTrue();
      }
    });
  });

  describe('two workspaces', () => {
    it('reads the next workspace afresh and shows nothing of the last', async () => {
      await create({ runs: { mine: { items: [summary('run-1', { title: 'Soda bread' })], nextCursor: null } } });
      expect(text()).toContain('Soda bread');

      await harness.navigateByUrl(`/${OTHER_SLUG}/workflows`);
      await settle();

      expect(text()).not.toContain('Soda bread');
      expect(definitionReads).toContain(OTHER_SLUG);
      expect(runReads.filter((read) => read.slug === OTHER_SLUG).map((read) => read.view).sort()).toEqual(['attention', 'completed', 'mine']);
      expect(goals()).not.toContain('Create my brand voice');
    });
  });
});
