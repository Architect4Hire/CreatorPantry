import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { Observable, of } from 'rxjs';

import { ConfirmService } from '../../core/confirm.service';
import { WorkspaceRole } from '../../models/auth.models';
import { ContentPipelineDraft, emptyContentPipelineDraft } from '../../models/content-pipeline.models';
import { ContentSeed, ContentSeedQuery } from '../../models/content-seed.models';
import { BrandProfileService } from '../../services/brand-profile.service';
import {
  ContentPipelineDraftOwner,
  ContentPipelineDraftService,
  ContentPipelineReadResult,
} from '../../services/content-pipeline-draft.service';
import { ContentSeedService, GenerateContentSeedOutcome } from '../../services/content-seed.service';
import { MyMembershipsState, WorkspaceMembershipService } from '../../services/workspace-membership.service';
import { CONTENT_PIPELINE_ROUTES } from './content-pipeline.routes';
import { ContentPipelineShellComponent } from './content-pipeline-shell.component';

@Component({ selector: 'cp-test-elsewhere', template: '<p>elsewhere</p>' })
class ElsewhereComponent {}

const SLUG = 'cozy-fall';
const OTHER_SLUG = 'other-kitchen';
/** The stub store keys the way the real one does: by workspace id and membership id, never by slug. */
const ownerKey = (owner: ContentPipelineDraftOwner): string => `${owner.workspaceId}.${owner.membershipId}`;
const delay = (ms: number): Promise<void> => new Promise((resolve) => setTimeout(resolve, ms));

const SEED: ContentSeed = {
  token: 'abc-123',
  cuisine: { key: 'thai', displayName: 'Thai', pinned: false },
  dishType: null,
  method: null,
  photographyStyle: null,
  channel: null,
  day: { day: 'Wednesday', pinned: false, theme: null },
  occasion: null,
  description: 'Develop a Thai dish.',
};

function membershipState(role: WorkspaceRole | null, status: 'ready' | 'loading' | 'error') {
  if (status !== 'ready') return signal<MyMembershipsState>({ status });

  return signal<MyMembershipsState>({
    status: 'ready',
    memberships:
      role === null
        ? []
        : [
            {
              workspaceId: 'w1',
              workspaceSlug: SLUG,
              workspaceName: 'Cozy Fall',
              membershipId: 'm1',
              role,
              status: 'Active' as const,
            },
            {
              workspaceId: 'w2',
              workspaceSlug: OTHER_SLUG,
              workspaceName: 'Other Kitchen',
              membershipId: 'm2',
              role,
              status: 'Active' as const,
            },
          ],
  });
}

let harness: RouterTestingHarness;
let shell: ContentPipelineShellComponent;
let router: Router;
let stored: Record<string, ContentPipelineDraft | null>;
let discardNext: boolean;
let writes: ContentPipelineDraft[];
let confirmAnswer: boolean;
let seedQueries: ContentSeedQuery[];

function generate(_slug: string, query: ContentSeedQuery): Observable<GenerateContentSeedOutcome> {
  seedQueries.push(query);
  return of<GenerateContentSeedOutcome>({ status: 'found', seed: SEED });
}

const draftStore = {
  read: (owner: ContentPipelineDraftOwner): ContentPipelineReadResult => {
    const discarded = discardNext;
    discardNext = false;
    return { draft: stored[ownerKey(owner)] ?? null, discarded };
  },
  write: (owner: ContentPipelineDraftOwner, draft: ContentPipelineDraft): void => {
    stored[ownerKey(owner)] = draft;
    writes.push(draft);
  },
  clear: (owner: ContentPipelineDraftOwner): void => {
    stored[ownerKey(owner)] = null;
  },
  purgeAll: (): void => {
    stored = {};
  },
};

async function create(
  options: {
    draft?: ContentPipelineDraft | null;
    discarded?: boolean;
    role?: WorkspaceRole | null;
    memberships?: 'ready' | 'loading' | 'error';
    url?: string;
  } = {},
): Promise<void> {
  stored = { 'w1.m1': options.draft ?? null };
  discardNext = options.discarded === true;
  writes = [];
  seedQueries = [];
  confirmAnswer = true;

  TestBed.resetTestingModule();
  await TestBed.configureTestingModule({
    providers: [
      provideRouter([
        { path: ':workspaceSlug/workflows/content-pipeline', children: CONTENT_PIPELINE_ROUTES },
        { path: ':workspaceSlug/workflows', component: ElsewhereComponent },
      ]),
      { provide: ContentPipelineDraftService, useValue: draftStore },
      { provide: ContentSeedService, useValue: { generate } },
      {
        provide: WorkspaceMembershipService,
        useValue: {
          state: membershipState(
            options.role === undefined ? 'Editor' : options.role,
            options.memberships ?? 'ready',
          ),
          ensureLoaded: () => Promise.resolve(),
          load: () => Promise.resolve(),
        },
      },
      { provide: BrandProfileService, useValue: { listContentChannels: () => Promise.resolve({ status: 'found', channels: [] }) } },
      { provide: ConfirmService, useValue: { confirm: () => Promise.resolve(confirmAnswer) } },
    ],
  }).compileComponents();

  router = TestBed.inject(Router);
  harness = await RouterTestingHarness.create();
  shell = await harness.navigateByUrl(
    options.url ?? `/${SLUG}/workflows/content-pipeline/setup`,
    ContentPipelineShellComponent,
  );
  await settle();
}

async function settle(): Promise<void> {
  for (let i = 0; i < 4; i += 1) {
    await delay(0);
    harness.detectChanges();
  }
}

function root(): HTMLElement {
  return harness.routeNativeElement as HTMLElement;
}

function text(): string {
  return root().textContent ?? '';
}

function button(label: string): HTMLButtonElement | null {
  return (
    (Array.from(root().querySelectorAll('button')).find((each) => each.textContent?.trim() === label) as
      | HTMLButtonElement
      | undefined) ?? null
  );
}

async function click(label: string): Promise<void> {
  button(label)!.click();
  await settle();
}

function filledDraft(overrides: Partial<ContentPipelineDraft> = {}): ContentPipelineDraft {
  const base = emptyContentPipelineDraft(new Date('2026-10-07T12:00:00Z'));
  return { ...base, config: { ...base.config, concept: 'A tight crop.' }, ...overrides };
}

describe('ContentPipelineShellComponent', () => {
  it('opens on the first step with the whole journey listed and an honest step figure', async () => {
    await create();

    expect(text()).toContain('Set it up');
    expect(text()).toContain('Step 1 of 6');
    expect(root().querySelectorAll('.step-list li').length).toBe(6);
    expect(root().querySelector('[aria-current="step"]')?.textContent).toContain('Set it up');
  });

  it('shows each step in the list with a word as well as a glyph, never colour alone', async () => {
    await create();

    const words = Array.from(root().querySelectorAll('.step-list .word')).map((span) => span.textContent?.trim());
    expect(words[0]).toBe('You are here');
    expect(words[1]).toBe('Not started');
  });

  it('names the step with a focusable heading and moves focus to it', async () => {
    await create();

    const heading = root().querySelector<HTMLElement>('#cp-pipeline-step-heading')!;
    expect(heading.getAttribute('tabindex')).toBe('-1');
    expect(document.activeElement).toBe(heading);
  });

  it('announces a move between steps politely, and says nothing on arrival', async () => {
    await create();

    const live = root().querySelector('[role="status"][aria-live="polite"]')!;
    expect(live.classList).toContain('cp-sr-only');
    // Nothing yet: focus landing on the heading is what announces the step you arrive on.
    expect(live.textContent?.trim()).toBe('');

    await click('Continue');

    expect(live.textContent).toContain('Pick an idea. Step 2 of 6.');
  });

  it("says a workspace it cannot find and one the creator is not in alike", async () => {
    await create({ role: null });

    expect(text()).toContain("couldn't find this workspace");
    expect(root().querySelector('#cp-pipeline-step-heading')).toBeNull();
    expect(root().querySelector('.step-list')).toBeNull();
  });

  it('stops a Viewer at the door rather than walking them into a wall', async () => {
    await create({ role: 'Viewer' });

    expect(text()).toContain("can't run the pipeline");
    expect(root().querySelector('#cp-pipeline-step-heading')).toBeNull();
  });

  it('reports memberships it could not read, with a way to try again', async () => {
    await create({ memberships: 'error' });

    expect(text()).toContain("workspaces couldn't be loaded");
    expect(button('Try again')).not.toBeNull();
  });

  it('offers to pick up a kept draft rather than dropping the creator back into it', async () => {
    await create({ draft: filledDraft({ furthestStep: 'idea' }) });

    expect(text()).toContain('Pick up where you left off');
    expect(text()).toContain('Pick an idea');

    await click('Continue');

    expect(router.url).toBe(`/${SLUG}/workflows/content-pipeline/idea`);
  });

  it('does not offer to resume a draft in which nothing was filled in', async () => {
    await create({ draft: emptyContentPipelineDraft() });

    expect(text()).not.toContain('Pick up where you left off');
    expect(text()).toContain('Set it up');
  });

  it('tells the creator when something kept here could not be read', async () => {
    await create({ discarded: true });

    expect(text()).toContain("couldn't be read");
  });

  it('keeps every change as it is made, so nothing lives only on screen', async () => {
    await create();

    const concept = root().querySelector<HTMLTextAreaElement>('#cp-pipeline-concept')!;
    concept.value = 'A tight crop of the first slice.';
    concept.dispatchEvent(new Event('input'));
    await settle();

    expect(writes.length).toBeGreaterThan(0);
    expect(stored['w1.m1']?.config.concept).toBe('A tight crop of the first slice.');
    expect(text()).toContain('Kept on this device');
  });

  it('carries on from the setup step, which asks nothing required', async () => {
    await create();

    expect(button('Continue')!.disabled).toBeFalse();
    await click('Continue');

    expect(router.url).toBe(`/${SLUG}/workflows/content-pipeline/idea`);
    expect(stored['w1.m1']?.furthestStep).toBe('idea');
  });

  it('will not carry on from the idea step until an idea has been picked', async () => {
    await create({ draft: filledDraft({ furthestStep: 'idea' }), url: `/${SLUG}/workflows/content-pipeline/idea` });
    await click('Continue');

    expect(button('Continue')!.disabled).toBeTrue();
    expect(text()).toContain('Pick an idea to carry on');

    await click('Suggest an idea');
    await click('Pick this idea');

    expect(button('Continue')!.disabled).toBeFalse();
  });

  it('sends the setup step’s channel and day as pins when an idea is asked for', async () => {
    const base = emptyContentPipelineDraft();
    await create({
      draft: { ...base, config: { ...base.config, channelKey: 'instagram', day: 'Friday' }, furthestStep: 'idea' },
      url: `/${SLUG}/workflows/content-pipeline/idea`,
    });
    await click('Continue');
    await click('Suggest an idea');

    expect(seedQueries[0].channel).toBe('instagram');
    expect(seedQueries[0].day).toBe('Friday');
  });

  it('goes back without taking away the steps already reached', async () => {
    await create({ draft: filledDraft({ furthestStep: 'idea' }), url: `/${SLUG}/workflows/content-pipeline/idea` });
    await click('Continue');
    await click('Back');

    expect(router.url).toBe(`/${SLUG}/workflows/content-pipeline/setup`);
    expect(stored['w1.m1']?.furthestStep).toBe('idea');
  });

  it('offers no Back on the first step', async () => {
    await create();

    expect(button('Back')).toBeNull();
  });

  it('sends a step nobody has reached yet back to the furthest one, without adding history', async () => {
    await create({ url: `/${SLUG}/workflows/content-pipeline/posts` });

    expect(router.url).toBe(`/${SLUG}/workflows/content-pipeline/setup`);
  });

  it('sends a step it does not know back to the furthest one', async () => {
    await create({ draft: filledDraft({ furthestStep: 'idea' }), url: `/${SLUG}/workflows/content-pipeline/nowhere` });

    expect(router.url).toBe(`/${SLUG}/workflows/content-pipeline/idea`);
  });

  it('redirects the bare pipeline route to its first step', async () => {
    await create({ url: `/${SLUG}/workflows/content-pipeline` });

    expect(router.url).toBe(`/${SLUG}/workflows/content-pipeline/setup`);
  });

  it('offers a step that is not built yet with no action at all, not a disabled one', async () => {
    const base = emptyContentPipelineDraft();
    await create({
      draft: {
        ...base,
        seed: { lastToken: null, keep: {}, accepted: SEED },
        prompt: { ...base.prompt, finalPrompt: 'A tight crop, soft light.' },
        furthestStep: 'images',
      },
      url: `/${SLUG}/workflows/content-pipeline/images`,
    });
    await click('Continue');

    expect(text()).toContain('coming soon');
    expect(button('Continue')).toBeNull();
    expect(text()).toContain('Step 4 of 6');
  });

  it('leaves without having to save, because nothing was ever only on screen', async () => {
    await create({ draft: filledDraft() });
    await click('Continue');
    await click('Save and exit');

    expect(router.url).toBe(`/${SLUG}/workflows`);
    expect(stored['w1.m1']?.config.concept).toBe('A tight crop.');
  });

  it('asks before starting over, and keeps everything when the answer is no', async () => {
    await create({ draft: filledDraft({ furthestStep: 'idea' }) });
    await click('Continue');

    confirmAnswer = false;
    await click('Start over');

    expect(stored['w1.m1']?.config.concept).toBe('A tight crop.');
  });

  it('throws the draft away when starting over is confirmed, and goes back to the first step', async () => {
    const base = emptyContentPipelineDraft();
    await create({
      draft: {
        ...base,
        config: { ...base.config, concept: 'A tight crop.', scene: ['marble slab'] },
        seed: { lastToken: 'abc-123', keep: {}, accepted: SEED },
        furthestStep: 'idea',
      },
      url: `/${SLUG}/workflows/content-pipeline/idea`,
    });
    await click('Continue');

    await click('Start over');

    expect(stored['w1.m1']).toBeNull();
    expect(router.url).toBe(`/${SLUG}/workflows/content-pipeline/setup`);
    expect(root().querySelector<HTMLTextAreaElement>('#cp-pipeline-concept')!.value).toBe('');
    expect(root().querySelector('input[id^="cp-pipeline-scene-"]')).toBeNull();
  });

  it('shows the step legend once, in the shell, rather than on each field', async () => {
    await create();

    const legends = root().querySelectorAll('.legend');
    expect(legends.length).toBe(1);
    expect(legends[0].textContent).toContain('optional');
  });

  it("keeps a draft under its workspace and member, never under the slug in the URL", async () => {
    await create({ draft: filledDraft() });
    await click('Continue');

    const concept = root().querySelector<HTMLTextAreaElement>('#cp-pipeline-concept')!;
    concept.value = 'Mine.';
    concept.dispatchEvent(new Event('input'));
    await settle();

    expect(Object.keys(stored)).toEqual(['w1.m1']);
    expect(stored['w1.m1']?.config.concept).toBe('Mine.');
  });

  it('shows nothing of one workspace once the route names another', async () => {
    await create({ draft: filledDraft({ furthestStep: 'idea' }) });
    await click('Continue');

    expect(text()).toContain('Pick an idea');
    expect(text()).toContain('Step 2 of 6');

    shell = await harness.navigateByUrl(
      `/${OTHER_SLUG}/workflows/content-pipeline/setup`,
      ContentPipelineShellComponent,
    );
    await settle();

    // The other workspace has no draft of its own, so neither the first one's progress nor its concept may
    // still be on screen.
    expect(text()).toContain('Step 1 of 6');
    expect(text()).not.toContain('Pick up where you left off');
    expect(root().querySelector<HTMLTextAreaElement>('#cp-pipeline-concept')!.value).toBe('');
  });

  it('waits for the memberships before judging the step in the URL against a draft', async () => {
    // Memberships never resolve, so nothing is known about who is asking. The shell must not decide the step
    // from a default draft and send the creator back to the start of their own run.
    await create({ draft: filledDraft({ furthestStep: 'idea' }), memberships: 'loading', url: `/${SLUG}/workflows/content-pipeline/idea` });

    expect(text()).toContain('Loading your pipeline');
    expect(router.url).toBe(`/${SLUG}/workflows/content-pipeline/idea`);
  });

  it('leaves without a guard, because leaving loses nothing', () => {
    const step = CONTENT_PIPELINE_ROUTES.find((route) => route.path === ':step');

    expect(step?.canDeactivate).toBeUndefined();
  });
});
