import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';

import { WorkspaceRole } from '../../models/auth.models';
import { ContentPipelineDraft, emptyContentPipelineDraft } from '../../models/content-pipeline.models';
import {
  ContentPipelineDraftOwner,
  ContentPipelineDraftService,
  ContentPipelineReadResult,
} from '../../services/content-pipeline-draft.service';
import { MyMembershipsState, WorkspaceMembershipService } from '../../services/workspace-membership.service';
import { WorkflowsHubComponent } from './workflows-hub.component';

@Component({ selector: 'cp-test-pipeline-step', template: '<p>a pipeline step</p>' })
class PipelineStepComponent {}

const SLUG = 'cozy-fall';
const OTHER_SLUG = 'other-kitchen';
const ownerKey = (owner: ContentPipelineDraftOwner): string => `${owner.workspaceId}.${owner.membershipId}`;
const delay = (ms: number): Promise<void> => new Promise((resolve) => setTimeout(resolve, ms));

function member(workspaceId: string, slug: string, membershipId: string, role: WorkspaceRole) {
  return { workspaceId, workspaceSlug: slug, workspaceName: slug, membershipId, role, status: 'Active' as const };
}

/** A draft that has reached the prompt step, which is step 3 of 4. */
function inProgress(concept = 'A tight crop.'): ContentPipelineDraft {
  const base = emptyContentPipelineDraft(new Date('2026-10-07T12:00:00Z'));

  return { ...base, config: { ...base.config, concept }, furthestStep: 'picture' };
}

let harness: RouterTestingHarness;
let memberships: ReturnType<typeof signal<MyMembershipsState>>;
let stored: Record<string, ContentPipelineDraft | null>;
/** Runs that are on a creative context: what this device kept for each, and the context it last had open. */
let runs: Record<string, { readonly contextId: string; readonly draft: ContentPipelineDraft } | undefined>;
let unreadable: Set<string>;
let reads: string[];
let loads: number;

const draftStore = {
  // As the real one does: an unreadable value is reported once and thrown away by the read that found it.
  read: (owner: ContentPipelineDraftOwner): ContentPipelineReadResult => {
    const key = ownerKey(owner);
    reads.push(key);

    return { draft: stored[key] ?? null, discarded: unreadable.delete(key) };
  },
  lastContextId: (owner: ContentPipelineDraftOwner): string | null => runs[ownerKey(owner)]?.contextId ?? null,
  readKept: (owner: ContentPipelineDraftOwner, contextId: string) => {
    const run = runs[ownerKey(owner)];

    return {
      kept: run !== undefined && run.contextId === contextId ? { draft: run.draft, unsent: null } : null,
      discarded: false,
    };
  },
};

async function settle(): Promise<void> {
  for (let i = 0; i < 3; i += 1) {
    await delay(0);
    harness.detectChanges();
  }
}

async function create(
  options: {
    drafts?: Record<string, ContentPipelineDraft | null>;
    runs?: Record<string, { readonly contextId: string; readonly draft: ContentPipelineDraft }>;
    unreadable?: readonly string[];
    role?: WorkspaceRole;
    state?: MyMembershipsState;
    url?: string;
  } = {},
): Promise<void> {
  stored = { ...(options.drafts ?? {}) };
  runs = { ...(options.runs ?? {}) };
  unreadable = new Set(options.unreadable ?? []);
  reads = [];
  loads = 0;

  const role = options.role ?? 'Editor';
  memberships = signal<MyMembershipsState>(
    options.state ?? {
      status: 'ready',
      memberships: [member('w1', SLUG, 'm1', role), member('w2', OTHER_SLUG, 'm2', role)],
    },
  );

  TestBed.resetTestingModule();
  await TestBed.configureTestingModule({
    providers: [
      provideRouter([
        { path: ':workspaceSlug/workflows', pathMatch: 'full', component: WorkflowsHubComponent },
        { path: ':workspaceSlug/workflows/content-pipeline/:step', component: PipelineStepComponent },
      ]),
      { provide: ContentPipelineDraftService, useValue: draftStore },
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
  await harness.navigateByUrl(options.url ?? `/${SLUG}/workflows`);
  await settle();
}

function root(): HTMLElement {
  return harness.routeNativeElement as HTMLElement;
}

function text(): string {
  return (root().textContent ?? '').replace(/\s+/g, ' ');
}

/** The Content Pipeline's own links. The card beside it has its own, read by {@link quickRecipeLinks}. */
function links(): HTMLAnchorElement[] {
  return Array.from(root().querySelectorAll('article[aria-labelledby="workflow-pipeline-heading"] a'));
}

function quickRecipeLinks(): HTMLAnchorElement[] {
  return Array.from(root().querySelectorAll('article[aria-labelledby="workflow-quick-recipe-heading"] a'));
}

describe('WorkflowsHubComponent', () => {
  describe('first use', () => {
    it('offers each journey that exists, in plain language, with one way in', async () => {
      await create();

      expect(root().querySelectorAll('article').length).toBe(2);
      expect(text()).toContain('Content Pipeline');
      expect(text()).toContain('From an idea to pictures and posts, one step at a time.');
      expect(links().map((link) => link.textContent?.trim())).toEqual(['Start']);
      expect(links()[0].getAttribute('href')).toBe(`/${SLUG}/workflows/content-pipeline/about`);
      expect(root().querySelector('cp-status-pill')).toBeNull();
    });

    it('treats a kept draft with nothing filled in as nothing to continue', async () => {
      await create({ drafts: { 'w1.m1': emptyContentPipelineDraft() } });

      expect(links().map((link) => link.textContent?.trim())).toEqual(['Start']);
    });

    it('reaches the first step by clicking, with no typed URL', async () => {
      await create();

      links()[0].click();
      await settle();

      expect(TestBed.inject(Router).url).toBe(`/${SLUG}/workflows/content-pipeline/about`);
      expect(text()).toContain('a pipeline step');
    });
  });

  describe('a draft in progress', () => {
    it('says Continue, names the step reached, and goes to it', async () => {
      await create({ drafts: { 'w1.m1': inProgress() } });

      expect(text()).toContain('You were on Plan the picture, step 2 of 4.');
      expect(text()).toContain('You last worked on it here on');
      expect(links().map((link) => link.textContent?.trim())).toEqual(['Continue']);
      expect(links()[0].getAttribute('href')).toBe(`/${SLUG}/workflows/content-pipeline/picture`);
    });

    it('says it is in progress in words, not by colour alone', async () => {
      await create({ drafts: { 'w1.m1': inProgress() } });

      expect(root().querySelector('cp-status-pill')?.textContent).toContain('In progress');
    });

    it("shows none of what the draft holds — only how far it got", async () => {
      await create({ drafts: { 'w1.m1': inProgress('A secret shoot.') } });

      expect(text()).not.toContain('A secret shoot.');
    });
  });

  describe('a run on a creative context', () => {
    it('sends Continue to the context this device last had open, at the step reached', async () => {
      await create({ runs: { 'w1.m1': { contextId: 'ctx-run', draft: inProgress() } } });

      expect(text()).toContain('You were on Plan the picture, step 2 of 4.');
      expect(links().map((link) => link.textContent?.trim())).toEqual(['Continue']);
      expect(links()[0].getAttribute('href')).toBe(`/${SLUG}/workflows/content-pipeline/context/ctx-run/picture`);
    });

    it('prefers a whole draft not yet on a context, which opening the pipeline will file', async () => {
      await create({
        drafts: { 'w1.m1': { ...inProgress(), furthestStep: 'picture' } },
        runs: { 'w1.m1': { contextId: 'ctx-run', draft: inProgress() } },
      });

      expect(links()[0].getAttribute('href')).toBe(`/${SLUG}/workflows/content-pipeline/picture`);
    });

    it("never sends one workspace to the other's context", async () => {
      await create({ runs: { 'w1.m1': { contextId: 'ctx-run', draft: inProgress() } } });

      await harness.navigateByUrl(`/${OTHER_SLUG}/workflows`);
      await settle();

      expect(links().map((link) => link.textContent?.trim())).toEqual(['Start']);
      expect(links()[0].getAttribute('href')).toBe(`/${OTHER_SLUG}/workflows/content-pipeline/about`);
    });

    it('reads nothing kept for a Viewer', async () => {
      await create({ role: 'Viewer', runs: { 'w1.m1': { contextId: 'ctx-run', draft: inProgress() } } });

      expect(links().length).toBe(0);
    });
  });

  describe('Quick Recipe', () => {
    it('is offered beside the pipeline, with one way in that reads alone in a list of links', async () => {
      await create();

      expect(text()).toContain('Name a dish and get a draft recipe and a lead picture for it, saved together.');
      expect(quickRecipeLinks().map((link) => link.textContent?.trim())).toEqual(['Start']);
      expect(quickRecipeLinks()[0].getAttribute('href')).toBe(`/${SLUG}/workflows/quick-recipe`);
      expect(quickRecipeLinks()[0].getAttribute('aria-label')).toBe('Start a Quick Recipe');
    });

    it('always says Start, whatever the pipeline has in progress', async () => {
      await create({ drafts: { 'w1.m1': inProgress() } });

      expect(quickRecipeLinks().map((link) => link.textContent?.trim())).toEqual(['Start']);
    });

    it('shows a Viewer the journey without a way in', async () => {
      await create({ role: 'Viewer' });

      expect(text()).toContain('Quick Recipe');
      expect(quickRecipeLinks()).toEqual([]);
    });

    it("points at the workspace on screen, never the one before it", async () => {
      await create();

      await harness.navigateByUrl(`/${OTHER_SLUG}/workflows`);
      await settle();

      expect(quickRecipeLinks()[0].getAttribute('href')).toBe(`/${OTHER_SLUG}/workflows/quick-recipe`);
    });
  });

  describe('an unreadable draft', () => {
    it('says so, and offers a fresh start', async () => {
      await create({ unreadable: ['w1.m1'] });

      expect(root().querySelector('cp-notice[role="status"]')?.textContent).toContain("couldn't be read");
      expect(links().map((link) => link.textContent?.trim())).toEqual(['Start']);
    });
  });

  describe('before the card can be trusted', () => {
    it('says it is loading while the memberships are being read, and offers no way in yet', async () => {
      await create({ state: { status: 'loading' } });

      expect(text()).toContain('Loading your workflows');
      expect(links()).toEqual([]);
      expect(reads).toEqual([]);
    });

    it('says the workspaces could not be loaded, and offers to try again', async () => {
      await create({ state: { status: 'error' } });

      expect(root().querySelector('[role="alert"]')?.textContent).toContain("couldn't be loaded");

      root().querySelector('button')!.click();

      expect(loads).toBe(1);
    });

    it('answers an unknown workspace and one the creator is not in alike, and reads nothing', async () => {
      await create({ url: '/never-created/workflows' });
      const unknown = text();

      await create({ state: { status: 'ready', memberships: [] } });

      expect(text()).toBe(unknown);
      expect(text()).toContain("We couldn't find this workspace");
      expect(reads).toEqual([]);
    });

    it('shows a Viewer the journey without a way in, and reads no draft for them', async () => {
      await create({ role: 'Viewer', drafts: { 'w1.m1': inProgress() } });

      expect(text()).toContain('Content Pipeline');
      expect(text()).toContain('view-only access');
      expect(links()).toEqual([]);
      expect(reads).toEqual([]);
    });
  });

  describe('accessibility', () => {
    it('has one page heading and names the card by its own', async () => {
      await create({ drafts: { 'w1.m1': inProgress() } });

      expect(root().querySelectorAll('h1').length).toBe(1);
      const card = root().querySelector('article')!;
      expect(root().querySelector('#' + card.getAttribute('aria-labelledby'))?.textContent?.trim()).toBe(
        'Content Pipeline',
      );
    });

    it('names each link by what it does to which journey, so it reads alone in a list of links', async () => {
      await create();
      expect(links()[0].getAttribute('aria-label')).toBe('Start the Content Pipeline');

      await create({ drafts: { 'w1.m1': inProgress() } });
      expect(links()[0].getAttribute('aria-label')).toBe('Continue the Content Pipeline');
    });

    it('nests no control inside another', async () => {
      await create({ drafts: { 'w1.m1': inProgress() } });

      for (const control of Array.from(root().querySelectorAll('a, button'))) {
        expect(control.querySelector('a, button')).toBeNull();
      }
    });
  });

  describe('two workspaces', () => {
    it("never shows one workspace's progress in the other", async () => {
      await create({ drafts: { 'w1.m1': inProgress() } });

      await harness.navigateByUrl(`/${OTHER_SLUG}/workflows`);
      await settle();

      expect(links().map((link) => link.textContent?.trim())).toEqual(['Start']);
      expect(links()[0].getAttribute('href')).toBe(`/${OTHER_SLUG}/workflows/content-pipeline/about`);
      expect(text()).not.toContain('You were on');
    });

    it('reads each workspace by its own id and membership, and each once', async () => {
      await create({ drafts: { 'w1.m1': inProgress() } });

      await harness.navigateByUrl(`/${OTHER_SLUG}/workflows`);
      await settle();

      expect(reads).toEqual(['w1.m1', 'w2.m2']);
    });

    it("shows the second workspace's own progress, and sends Continue there", async () => {
      const there = { ...inProgress(), furthestStep: 'images' as const };
      await create({ drafts: { 'w1.m1': inProgress(), 'w2.m2': there } });

      await harness.navigateByUrl(`/${OTHER_SLUG}/workflows`);
      await settle();

      expect(text()).toContain('You were on Make the images, step 3 of 4.');
      expect(links()[0].getAttribute('href')).toBe(`/${OTHER_SLUG}/workflows/content-pipeline/images`);
    });

    it("does not carry one workspace's unreadable-draft notice into the other", async () => {
      await create({ unreadable: ['w1.m1'] });

      await harness.navigateByUrl(`/${OTHER_SLUG}/workflows`);
      await settle();

      expect(text()).not.toContain("couldn't be read");
    });

    it("never shows one member their colleague's progress in the same workspace", async () => {
      await create({ drafts: { 'w1.m1': inProgress() } });

      memberships.set({ status: 'ready', memberships: [member('w1', SLUG, 'm-colleague', 'Editor')] });
      await settle();

      expect(reads).toEqual(['w1.m1', 'w1.m-colleague']);
      expect(links().map((link) => link.textContent?.trim())).toEqual(['Start']);
    });
  });
});
