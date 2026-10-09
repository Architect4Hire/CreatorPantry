import { Location } from '@angular/common';
import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { Observable, of } from 'rxjs';

import { ConfirmService } from '../../core/confirm.service';
import { WorkspaceRole } from '../../models/auth.models';
import {
  ContentPipelineDraft,
  ContentPipelineKeptRun,
  decodeContentPipelineRun,
  emptyContentPipelineDraft,
  encodeContentPipelineDraft,
  encodeContentPipelineRun,
} from '../../models/content-pipeline.models';
import { ContentSeed, ContentSeedQuery } from '../../models/content-seed.models';
import { RequestPhotographyConceptsRequest } from '../../models/photography-concept.models';
import { AiAllowanceState, AiUsageService } from '../../services/ai-usage.service';
import { BrandSourceDocumentService } from '../../services/brand-source-document.service';
import { ImagePromptService } from '../../services/image-prompt.service';
import { PhotographyConceptService } from '../../services/photography-concept.service';
import { ReferenceImageService } from '../../services/reference-image.service';
import { AuthService, SessionState } from '../../services/auth.service';
import { BrandProfileService } from '../../services/brand-profile.service';
import { ContentSeedService, GenerateContentSeedOutcome } from '../../services/content-seed.service';
import { CreativeContextSession } from '../../services/creative-context-session';
import { FakeCreativeContextService } from '../../services/creative-context.fake';
import { CreativeContextService } from '../../services/creative-context.service';
import { DamAssetService } from '../../services/dam-asset.service';
import { GeneratedImageService } from '../../services/generated-image.service';
import { FakeRecipeLibrary } from '../../services/recipe-library.fake';
import { RecipeService } from '../../services/recipe.service';
import { MyMembershipsState, WorkspaceMembershipService } from '../../services/workspace-membership.service';
import { CONTENT_PIPELINE_ROUTES } from './content-pipeline.routes';
import { ContentPipelineShellComponent } from './content-pipeline-shell.component';

@Component({ selector: 'cp-test-elsewhere', template: '<p>elsewhere</p>' })
class ElsewhereComponent {}

const SLUG = 'cozy-fall';
const OTHER_SLUG = 'other-kitchen';
/** The context a run in progress is on, in the first workspace. */
const CTX = 'ctx-run';
const PIPELINE = `/${SLUG}/workflows/content-pipeline`;
const RUN = `${PIPELINE}/context/${CTX}`;
/** Where this device keeps things for the first workspace's member: by workspace id and membership id, never by slug. */
const UNFILED_KEY = 'cp.pipeline.w1.m1';
const LAST_KEY = 'cp.pipeline.last.w1.m1';
const keptKey = (contextId: string): string => `${UNFILED_KEY}.${contextId}`;
const delay = (ms: number): Promise<void> => new Promise((resolve) => setTimeout(resolve, ms));

const SEED: ContentSeed = {
  token: 'abc-123',
  cuisine: { key: 'thai', displayName: 'Thai', pinned: false, fromRecipe: false },
  dishType: null,
  method: null,
  photographyStyle: null,
  channel: null,
  day: { day: 'Wednesday', pinned: false, theme: null },
  occasion: null,
  description: 'Develop a Thai dish.',
  recipe: null,
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
let server: FakeCreativeContextService;
let library: FakeRecipeLibrary;
let confirmAnswer: boolean;
let seedQueries: ContentSeedQuery[];
let seedToReturn: ContentSeed;
let conceptRequests: { readonly slug: string; readonly request: RequestPhotographyConceptsRequest }[];

function generate(_slug: string, query: ContentSeedQuery): Observable<GenerateContentSeedOutcome> {
  seedQueries.push(query);
  return of<GenerateContentSeedOutcome>({ status: 'found', seed: seedToReturn });
}

/** What this device keeps for one context, read the way the product reads it. */
function keptFor(contextId: string): ContentPipelineKeptRun | null {
  return decodeContentPipelineRun(localStorage.getItem(keptKey(contextId)));
}

/**
 * Open the pipeline.
 *
 * `run` is a run in progress: its channel, day and picture are on the server as context {@link CTX}, the rest
 * is kept on this device for that context, and it is the one this device last had open. `unfiled` is a whole
 * draft from before runs lived on a context, sitting on this device alone.
 */
async function create(
  options: {
    run?: ContentPipelineDraft;
    unfiled?: ContentPipelineDraft;
    stored?: string;
    role?: WorkspaceRole | null;
    memberships?: 'ready' | 'loading' | 'error';
    url?: string;
    offline?: boolean;
    setup?: (server: FakeCreativeContextService) => void;
  } = {},
): Promise<void> {
  localStorage.clear();
  seedQueries = [];
  seedToReturn = SEED;
  conceptRequests = [];
  confirmAnswer = true;
  server = new FakeCreativeContextService();
  library = new FakeRecipeLibrary([
    { slug: SLUG, id: 'r-soda', title: 'Soda bread', versionIds: ['v-soda-1', 'v-soda-2'] },
    { slug: OTHER_SLUG, id: 'r-theirs', title: 'Their soda bread', versionIds: ['v-theirs-1'] },
  ]);

  if (options.run) {
    const { config } = options.run;
    server.seed(SLUG, CTX, {
      channelKeys: config.channelKey === null ? [] : [config.channelKey],
      day: config.day,
      pictureBrief: config.concept === '' ? null : config.concept,
    });
    localStorage.setItem(keptKey(CTX), encodeContentPipelineRun(options.run, null));
    localStorage.setItem(LAST_KEY, CTX);
  }
  if (options.unfiled) localStorage.setItem(UNFILED_KEY, encodeContentPipelineDraft(options.unfiled));
  if (options.stored !== undefined) localStorage.setItem(UNFILED_KEY, options.stored);
  options.setup?.(server);
  server.offline = options.offline === true;

  TestBed.resetTestingModule();
  await TestBed.configureTestingModule({
    providers: [
      provideRouter([
        { path: ':workspaceSlug/workflows/content-pipeline', children: CONTENT_PIPELINE_ROUTES },
        { path: ':workspaceSlug/workflows', component: ElsewhereComponent },
      ]),
      // The real device store, over the real localStorage: what is kept where is what these specs are about.
      {
        provide: AuthService,
        useValue: { session: signal<SessionState>({ status: 'authenticated', displayName: 'Rae' }).asReadonly() },
      },
      { provide: CreativeContextService, useValue: server },
      { provide: RecipeService, useValue: library },
      // The reference panel's library picker and linked-asset card. Nothing here chooses a library picture, so
      // the library is empty: an unreachable one would put a second "Try again" on the page.
      {
        provide: DamAssetService,
        useValue: {
          search: () => of({ status: 'found' as const, page: { items: [], nextCursor: null, totalCount: 0 } }),
          detail: () => of({ status: 'unavailable' as const }),
          content: () => of({ status: 'unavailable' as const }),
          versionContent: () => of({ status: 'unavailable' as const }),
        },
      },
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
      // The prompt step's clients. Only the concept request is looked at: what it is sent is what FLU-004 is
      // about. None of them answers, so nothing is watched and nothing is spent.
      {
        provide: PhotographyConceptService,
        useValue: {
          request: (slug: string, request: RequestPhotographyConceptsRequest) => {
            conceptRequests.push({ slug, request });

            return Promise.resolve({ status: 'unavailable' as const });
          },
          watch: () => of(),
        },
      },
      { provide: ImagePromptService, useValue: { request: () => Promise.resolve({ status: 'unavailable' as const }), watch: () => of() } },
      { provide: ReferenceImageService, useValue: { request: () => Promise.resolve({ status: 'unavailable' as const }), watch: () => of() } },
      {
        provide: AiUsageService,
        useValue: { allowance: signal<AiAllowanceState>({ kind: 'unknown' }), ensureLoaded: () => Promise.resolve() },
      },
      {
        provide: BrandSourceDocumentService,
        useValue: {
          searchLibrary: () => of({ status: 'ok', page: { items: [], nextCursor: null } }),
          upload: () => Promise.resolve({ status: 'failed' as const, reason: 'unavailable' as const }),
        },
      },
      // The images step is a client of this one. The shell's own tests are about navigation and the kept
      // run, so nothing here is asked of the server: a run that is never resumed polls nothing.
      {
        provide: GeneratedImageService,
        useValue: {
          watch: () => of({ status: 'unavailable' }),
          preview: () => of({ status: 'unavailable' }),
          request: () => Promise.resolve({ status: 'unavailable' }),
          reject: () => Promise.resolve({ status: 'unavailable' }),
          downloadUrl: () => null,
        },
      },
    ],
  }).compileComponents();

  router = TestBed.inject(Router);
  harness = await RouterTestingHarness.create();
  shell = await harness.navigateByUrl(options.url ?? `${PIPELINE}/setup`, ContentPipelineShellComponent);
  await settle();
}

async function settle(): Promise<void> {
  for (let i = 0; i < 4; i += 1) {
    await delay(0);
    harness.detectChanges();
  }
}

/** The session of the shell on screen now — a navigation between address shapes builds a new one. */
function session(): CreativeContextSession {
  return harness.routeDebugElement!.injector.get(CreativeContextSession);
}

/** Send whatever the autosave is waiting to send, without waiting out its delay. */
async function saved(): Promise<void> {
  await session().flush();
  await settle();
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

/** Pick one of the three ways to work, by position: description, idea, combined. */
async function chooseBrief(index: number): Promise<void> {
  root().querySelectorAll<HTMLInputElement>('#cp-pipeline-brief-choice input[type="radio"]')[index].click();
  await settle();
}

async function typeConcept(value: string): Promise<void> {
  const concept = root().querySelector<HTMLTextAreaElement>('#cp-pipeline-concept')!;
  concept.value = value;
  concept.dispatchEvent(new Event('input'));
  await settle();
}

function conceptValue(): string {
  return root().querySelector<HTMLTextAreaElement>('#cp-pipeline-concept')!.value;
}

function filledDraft(overrides: Partial<ContentPipelineDraft> = {}): ContentPipelineDraft {
  const base = emptyContentPipelineDraft(new Date('2026-10-07T12:00:00Z'));
  return { ...base, config: { ...base.config, concept: 'A tight crop.' }, ...overrides };
}

/** A draft that has got as far as the images step: an idea picked and a prompt written. */
function reachedImages(): ContentPipelineDraft {
  const base = emptyContentPipelineDraft(new Date('2026-10-07T12:00:00Z'));

  return {
    ...base,
    seed: { lastToken: null, keep: {}, accepted: SEED },
    prompt: { ...base.prompt, finalPrompt: 'A tight crop, soft light.', promptSource: 'creator' },
    furthestStep: 'images',
  };
}

describe('ContentPipelineShellComponent', () => {
  afterEach(() => localStorage.clear());

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
    expect(server.creates.length).toBe(0);
  });

  it('reports memberships it could not read, with a way to try again', async () => {
    await create({ memberships: 'error' });

    expect(text()).toContain("workspaces couldn't be loaded");
    expect(button('Try again')).not.toBeNull();
  });

  it('offers to pick up the run this device last had open, and opens it at its own address', async () => {
    await create({ run: filledDraft({ furthestStep: 'idea' }) });

    expect(text()).toContain('Pick up where you left off');
    expect(text()).toContain('Pick an idea');
    expect(server.gets.length).withContext('offered from this device, without asking the server').toBe(0);

    await click('Continue');

    expect(router.url).toBe(`${RUN}/idea`);
    expect(text()).toContain('Step 2 of 6');
  });

  it('does not offer to resume when nothing was ever filled in', async () => {
    await create({ unfiled: emptyContentPipelineDraft() });

    expect(text()).not.toContain('Pick up where you left off');
    expect(text()).toContain('Set it up');
    expect(server.creates.length).withContext('an empty draft is not worth a context').toBe(0);
  });

  it('tells the creator when something kept here could not be read', async () => {
    await create({ stored: '{not json' });

    expect(text()).toContain("couldn't be read");
    expect(localStorage.getItem(UNFILED_KEY)).toBeNull();
  });

  it('carries on from the setup step, which asks nothing required', async () => {
    await create();

    expect(button('Continue')!.disabled).toBeFalse();
    await click('Continue');

    expect(router.url).toBe(`${PIPELINE}/idea`);
    // Nothing has been answered, so there is still no context: only how far the creator got, on this device.
    expect(server.creates.length).toBe(0);
    expect(JSON.parse(localStorage.getItem(UNFILED_KEY)!).furthestStep).toBe('idea');
  });

  it('will not carry on from the idea step until an idea has been picked', async () => {
    await create({ run: filledDraft({ furthestStep: 'idea' }), url: `${RUN}/idea` });

    expect(button('Continue')!.disabled).toBeTrue();
    expect(text()).toContain('Pick an idea to carry on');

    await click('Suggest an idea');
    await click('Pick this idea');

    // The run has a description too, so picking is not the whole answer: which to work from is asked.
    expect(button('Continue')!.disabled).toBeTrue();
    expect(text()).toContain('Choose what to work from to carry on');

    await chooseBrief(0);

    expect(button('Continue')!.disabled).toBeFalse();
  });

  describe('the picture described in setup', () => {
    const MINE = 'A tight crop.';
    const IDEA = 'Develop a Thai dish.';

    /** A run with a description, at the idea step, with the idea picked and one way of working chosen. */
    async function chosen(index: number): Promise<void> {
      await create({ run: filledDraft({ furthestStep: 'idea' }), url: `${RUN}/idea` });
      await click('Suggest an idea');
      await click('Pick this idea');
      await chooseBrief(index);
    }

    async function planLooks(): Promise<void> {
      await click('Continue');
      await click('Plan some looks');
    }

    it('reaches the photography-concept request as written when the creator works from their description', async () => {
      await chosen(0);
      await planLooks();

      expect(conceptRequests.length).toBe(1);
      expect(conceptRequests[0].slug).toBe(SLUG);
      expect(conceptRequests[0].request.creatorConcept).toBe(MINE);
    });

    it('is not sent when the creator works from the idea — and is still theirs, unchanged', async () => {
      await chosen(1);
      await planLooks();

      expect(conceptRequests[0].request.creatorConcept).toBe(IDEA);
      await saved();
      expect(server.stored(SLUG, CTX)?.pictureBrief).toBe(MINE);
    });

    it('reaches the request first, with the idea below it, when the creator combines them', async () => {
      await chosen(2);
      await planLooks();

      expect(conceptRequests[0].request.creatorConcept).toBe(`${MINE}\n\n${IDEA}`);
    });

    it('is shown, editable, at the head of the prompt step, and the edit is what is sent', async () => {
      await chosen(0);
      await click('Continue');

      const box = root().querySelector<HTMLTextAreaElement>('#cp-pipeline-brief-text')!;
      expect(box.value).toBe(MINE);

      box.value = 'A tight crop, and steam.';
      box.dispatchEvent(new Event('input'));
      await settle();
      await click('Plan some looks');

      expect(conceptRequests[0].request.creatorConcept).toBe('A tight crop, and steam.');
      await saved();
      expect(server.stored(SLUG, CTX)?.workingBrief).toBe('A tight crop, and steam.');
      expect(server.stored(SLUG, CTX)?.pictureBrief).withContext('the description is never rewritten').toBe(MINE);
    });

    it('stores the choice and the brief on the context, so a resumed run shows the same brief', async () => {
      await chosen(2);
      await saved();

      expect(server.stored(SLUG, CTX)?.briefSource).toBe('Combined');
      expect(server.stored(SLUG, CTX)?.workingBrief).toBe(`${MINE}\n\n${IDEA}`);
      expect(keptFor(CTX)?.draft.config.brief).withContext('not kept on this device once saved').toBe('');

      // Another device: nothing kept locally, only the context.
      localStorage.clear();
      await harness.navigateByUrl(`/${SLUG}/workflows`);
      shell = await harness.navigateByUrl(`${RUN}/setup`, ContentPipelineShellComponent);
      await settle();

      const fields = session().fields();
      expect(fields.briefSource).toBe('Combined');
      expect(fields.workingBrief).toBe(`${MINE}\n\n${IDEA}`);
      expect(fields.pictureBrief).toBe(MINE);
    });

    it('follows a correction to the description while the brief is unedited, and stops once it is edited', async () => {
      await chosen(0);
      await click('Back');
      await typeConcept('A tight crop of the first slice.');
      await saved();

      expect(server.stored(SLUG, CTX)?.workingBrief).toBe('A tight crop of the first slice.');

      await click('Continue');
      await click('Continue');
      const box = root().querySelector<HTMLTextAreaElement>('#cp-pipeline-brief-text')!;
      box.value = 'My edited brief.';
      box.dispatchEvent(new Event('input'));
      await settle();
      await click('Back');
      await click('Back');
      await typeConcept('Something else.');
      await saved();

      expect(server.stored(SLUG, CTX)?.workingBrief).toBe('My edited brief.');
      expect(server.stored(SLUG, CTX)?.pictureBrief).toBe('Something else.');
    });

    it('asks again when the idea a brief was made from is un-picked', async () => {
      await chosen(1);
      await click('Change the idea');

      expect(session().fields().briefSource).toBeNull();
      expect(session().fields().workingBrief).toBe('');
      expect(session().fields().pictureBrief).toBe(MINE);
    });
  });

  it('sends the setup step’s channel and day as pins when an idea is asked for', async () => {
    const base = emptyContentPipelineDraft();
    await create({
      run: { ...base, config: { ...base.config, channelKey: 'instagram', day: 'Friday' }, furthestStep: 'idea' },
      url: `${RUN}/idea`,
    });
    await click('Suggest an idea');

    expect(seedQueries[0].channel).toBe('instagram');
    expect(seedQueries[0].day).toBe('Friday');
  });

  it('goes back without taking away the steps already reached', async () => {
    await create({ run: filledDraft({ furthestStep: 'idea' }), url: `${RUN}/idea` });
    await click('Back');

    expect(router.url).toBe(`${RUN}/setup`);
    await click('Continue');
    expect(router.url).toBe(`${RUN}/idea`);
  });

  it('offers no Back on the first step', async () => {
    await create();

    expect(button('Back')).toBeNull();
  });

  it('sends a step nobody has reached yet back to the furthest one, without adding history', async () => {
    await create({ url: `${PIPELINE}/posts` });

    expect(router.url).toBe(`${PIPELINE}/setup`);
  });

  it('sends a step it does not know back to the furthest one', async () => {
    await create({ run: filledDraft({ furthestStep: 'idea' }), url: `${RUN}/nowhere` });

    expect(router.url).toBe(`${RUN}/idea`);
  });

  describe('opened for a creative context', () => {
    it('opens at the first step when the address names no step', async () => {
      await create({ url: RUN, setup: (fake) => fake.seed(SLUG, CTX) });

      expect(router.url).toBe(`${RUN}/setup`);
    });

    it('keeps the context in the address when moving on and back', async () => {
      await create({ url: `${RUN}/setup`, setup: (fake) => fake.seed(SLUG, CTX) });

      await click('Continue');
      expect(router.url).toBe(`${RUN}/idea`);

      await click('Back');
      expect(router.url).toBe(`${RUN}/setup`);
    });

    it('keeps the context when it sends an unreached step back to the furthest one', async () => {
      await create({ url: `${RUN}/posts`, setup: (fake) => fake.seed(SLUG, CTX) });

      expect(router.url).toBe(`${RUN}/setup`);
    });

    it('still treats a bare `context` segment as a step it does not know', async () => {
      await create({ url: `${PIPELINE}/context` });

      expect(router.url).toBe(`${PIPELINE}/setup`);
    });

    it('shows what the context holds, with nothing kept on this device — a fresh browser profile', async () => {
      await create({
        url: `${RUN}/setup`,
        setup: (fake) =>
          fake.seed(SLUG, CTX, {
            channelKeys: ['instagram'],
            day: 'Friday',
            weeklyThemeKey: 'fish-friday',
            pictureBrief: 'A tight crop of the first slice.',
          }),
      });

      expect(conceptValue()).toBe('A tight crop of the first slice.');
      expect(text()).toContain('Step 1 of 6');
      expect(text()).toContain('Saved');
      expect(server.patches.length).withContext('reading is not an edit').toBe(0);
      expect(localStorage.getItem(keptKey(CTX))).withContext('opening leaves no record').toBeNull();

      await click('Continue');
      await click('Suggest an idea');

      expect(seedQueries[0].channel).toBe('instagram');
      expect(seedQueries[0].day).toBe('Friday');
    });

    it('reads an id that does not resolve as not found, never as an empty run', async () => {
      await create({ url: `${RUN}/setup` });

      expect(text()).toContain("couldn't find this piece of work");
      expect(root().querySelector('#cp-pipeline-step-heading')).toBeNull();
      expect(root().querySelector('#cp-pipeline-concept')).toBeNull();
      expect(server.creates.length).toBe(0);
    });

    it("reads another workspace's context as not found", async () => {
      await create({ url: `${RUN}/setup`, setup: (fake) => fake.seed(OTHER_SLUG, CTX, { pictureBrief: 'Theirs.' }) });

      expect(text()).toContain("couldn't find this piece of work");
      expect(text()).not.toContain('Theirs.');
    });

    it('says so when the context could not be read, and reads it on a retry', async () => {
      await create({ url: `${RUN}/setup`, offline: true, setup: (fake) => fake.seed(SLUG, CTX, { pictureBrief: 'A tight crop.' }) });

      expect(text()).toContain("couldn't be loaded right now");
      expect(root().querySelector('#cp-pipeline-concept')).toBeNull();

      server.offline = false;
      await click('Try again');

      expect(conceptValue()).toBe('A tight crop.');
    });
  });

  it('redirects the bare pipeline route to its first step', async () => {
    await create({ url: PIPELINE });

    expect(router.url).toBe(`${PIPELINE}/setup`);
  });

  it('offers a step that is not built yet with no action at all, not a disabled one', async () => {
    await create({ run: { ...reachedImages(), furthestStep: 'posts' }, url: `${RUN}/posts` });

    expect(text()).toContain('coming soon');
    expect(button('Continue')).toBeNull();
    expect(text()).toContain('Step 5 of 6');
  });

  it('holds the images step at Continue until a picture has been kept, and says why', async () => {
    await create({ run: reachedImages(), url: `${RUN}/images` });

    expect(text()).toContain('Make the pictures');
    expect(text()).not.toContain('coming soon');
    expect(button('Continue')?.disabled).withContext('nothing kept yet').toBeTrue();
    expect(text()).toContain('Keep at least one picture to carry on.');
  });

  it('lets the images step continue once a picture is kept', async () => {
    const reached = reachedImages();
    await create({
      run: { ...reached, images: { ...reached.images, operationId: 'op-1', keepers: ['img-1'] } },
      url: `${RUN}/images`,
    });

    expect(button('Continue')?.disabled).toBeFalse();
  });

  it('shows the step legend once, in the shell, rather than on each field', async () => {
    await create();

    const legends = root().querySelectorAll('.legend');
    expect(legends.length).toBe(1);
    expect(legends[0].textContent).toContain('optional');
  });

  describe('a linked recipe', () => {
    async function linkSodaBread(): Promise<void> {
      const box = root().querySelector<HTMLInputElement>('#cp-pipeline-recipe-search')!;
      box.focus();
      box.value = 'soda';
      box.dispatchEvent(new Event('input'));
      await delay(300);
      await settle();

      const option = root().querySelector<HTMLElement>('#cp-pipeline-recipe [role="option"]')!;
      option.dispatchEvent(new MouseEvent('mousedown', { bubbles: true }));
      option.click();
      await settle();
    }

    /** From setup to the prompt step, with an idea picked on the way, then plan some looks. */
    async function planLooks(): Promise<void> {
      await click('Continue');
      await click('Suggest an idea');
      await click('Pick this idea');
      await click('Continue');
      await click('Plan some looks');
    }

    it('is chosen on the setup step and stored on the context, pinned to its current version', async () => {
      await create({ url: `${RUN}/setup`, setup: (fake) => fake.seed(SLUG, CTX) });

      await linkSodaBread();

      expect(server.stored(SLUG, CTX)?.references.map((each) => [each.kind, each.recipeId, each.recipeVersionId])).toEqual([
        ['Recipe', 'r-soda', 'v-soda-2'],
      ]);
      expect(text()).toContain('Version 2, as it was when you linked it.');
      expect(root().querySelector('[role="status"][aria-live="polite"]')?.textContent).toContain('Soda bread is linked.');
    });

    it('is named, with its pinned version, on the photography-concept request', async () => {
      await create({ url: `${RUN}/setup`, setup: (fake) => fake.seed(SLUG, CTX) });
      await linkSodaBread();

      await planLooks();

      expect(conceptRequests.length).toBe(1);
      expect(conceptRequests[0].request.recipeId).toBe('r-soda');
      expect(conceptRequests[0].request.recipeVersionId).toBe('v-soda-2');
    });

    it('is not named at all when the run has no recipe linked', async () => {
      await create({ url: `${RUN}/setup`, setup: (fake) => fake.seed(SLUG, CTX) });

      await planLooks();

      expect(conceptRequests[0].request.recipeId).toBeNull();
      expect(conceptRequests[0].request.recipeVersionId).toBeNull();
    });

    it('gives a run its context when it is the first thing chosen, and the run can be picked up again', async () => {
      await create();

      await linkSodaBread();

      expect(server.creates.length).toBe(1);
      const contextId = session().context()!.id;
      expect(server.stored(SLUG, contextId)?.references[0].recipeId).toBe('r-soda');
      expect(localStorage.getItem(LAST_KEY)).toBe(contextId);
    });

    it('is still the pinned version after the recipe changes, until the creator re-pins it', async () => {
      await create({
        url: `${RUN}/setup`,
        setup: (fake) =>
          fake.seed(SLUG, CTX, {
            references: [fake.reference({ kind: 'Recipe', recipeId: 'r-soda', recipeVersionId: 'v-soda-1' }, 0)],
          }),
      });

      expect(text()).toContain('This recipe has changed since you linked it');
      await planLooks();
      expect(conceptRequests[0].request.recipeVersionId).toBe('v-soda-1');
    });

    it("cannot be another workspace's recipe: it is not offered, and one named by id is refused", async () => {
      await create({ url: `${RUN}/setup`, setup: (fake) => fake.seed(SLUG, CTX) });
      await linkSodaBread();

      expect(library.searches.every((search) => search.slug === SLUG)).toBeTrue();
      expect(text()).not.toContain('Their soda bread');

      server.unusableRecipeIds.add('r-theirs');
      const outcome = await session().addReference({ kind: 'Recipe', recipeId: 'r-theirs', recipeVersionId: 'v-theirs-1' });

      expect(outcome).toBe('source_unavailable');
      expect(server.stored(SLUG, CTX)?.references.map((each) => each.recipeId)).toEqual(['r-soda']);
    });
  });

  describe('keeping a run on its creative context', () => {
    it('gives a run its context on the first answer, and puts that context in the address', async () => {
      await create();
      expect(server.creates.length).withContext('arriving makes nothing').toBe(0);

      await typeConcept('A tight crop of the first slice.');

      expect(server.creates.length).toBe(1);
      expect(server.creates[0].slug).toBe(SLUG);
      const contextId = session().context()!.id;
      expect(router.url).withContext('the screen was not replaced').toBe(`${PIPELINE}/setup`);
      expect(TestBed.inject(Location).path()).toBe(`${PIPELINE}/context/${contextId}/setup`);
      expect(conceptValue()).toBe('A tight crop of the first slice.');
    });

    it('saves the picture to the context, and keeps no copy of it on this device once it is there', async () => {
      await create({ run: filledDraft(), url: `${RUN}/setup` });

      await typeConcept('A tight crop of the first slice.');
      await saved();

      expect(server.stored(SLUG, CTX)?.pictureBrief).toBe('A tight crop of the first slice.');
      expect(text()).toContain('Saved');
      expect(localStorage.getItem(keptKey(CTX))).not.toContain('first slice');
      expect(keptFor(CTX)?.unsent).toBeNull();
    });

    it('keeps the rest of the run on this device under the context, the workspace and the member', async () => {
      await create({ run: filledDraft({ furthestStep: 'idea' }), url: `${RUN}/idea` });

      await click('Suggest an idea');
      await click('Pick this idea');

      expect(keptFor(CTX)?.draft.seed.accepted?.token).toBe('abc-123');
      const keys = Object.keys(localStorage).filter((key) => key.startsWith('cp.pipeline.'));
      expect(keys.sort()).toEqual([keptKey(CTX), LAST_KEY].sort());
    });

    it("takes the picked idea's theme when the idea is for the run's day", async () => {
      const base = emptyContentPipelineDraft();
      await create({
        run: { ...base, config: { ...base.config, day: 'Wednesday' }, furthestStep: 'idea' },
        url: `${RUN}/idea`,
      });
      seedToReturn = {
        ...SEED,
        day: { day: 'Wednesday', pinned: true, theme: { key: 'midweek-noodles', displayName: 'Midweek noodles', pinned: false, fromRecipe: false } },
      };

      await click('Suggest an idea');
      await click('Pick this idea');
      await saved();

      expect(server.stored(SLUG, CTX)?.weeklyThemeKey).toBe('midweek-noodles');
    });

    it('leaves without having to save, sending what was still waiting', async () => {
      await create({ run: filledDraft(), url: `${RUN}/setup` });
      await typeConcept('Typed, then left.');

      await click('Save and exit');

      expect(router.url).toBe(`/${SLUG}/workflows`);
      expect(server.stored(SLUG, CTX)?.pictureBrief).toBe('Typed, then left.');
    });
  });

  describe('a draft from before runs lived on a context', () => {
    const OLD = filledDraft({
      config: { ...emptyContentPipelineDraft().config, channelKey: 'instagram', day: 'Friday', concept: 'A tight crop.', scene: ['marble slab'] },
      seed: { lastToken: 'abc-123', keep: {}, accepted: SEED },
      furthestStep: 'prompt',
    });

    it('is moved onto a new context once, cleared from where it was, and opened where the creator had got to', async () => {
      await create({ unfiled: OLD });

      expect(server.creates.length).toBe(1);
      expect(server.creates[0].draft).toEqual({ channelKeys: ['instagram'], day: 'Friday', pictureBrief: 'A tight crop.' });
      const contextId = session().context()!.id;
      expect(router.url).toBe(`${PIPELINE}/context/${contextId}/prompt`);
      expect(text()).toContain('Step 3 of 6');

      expect(localStorage.getItem(UNFILED_KEY)).withContext('the old draft is cleared').toBeNull();
      expect(localStorage.getItem(LAST_KEY)).toBe(contextId);
      const kept = keptFor(contextId)!;
      expect(kept.draft.seed.accepted?.token).withContext('the picked idea came along').toBe('abc-123');
      expect(kept.draft.config.scene).toEqual(['marble slab']);
      expect(kept.draft.config.concept).withContext('the picture is on the context, not copied here').toBe('');
    });

    it('is not moved twice: coming back finds the run, not a draft to file', async () => {
      await create({ unfiled: OLD });
      const contextId = session().context()!.id;

      shell = await harness.navigateByUrl(`${PIPELINE}/setup`, ContentPipelineShellComponent);
      await settle();

      expect(server.creates.length).toBe(1);
      expect(text()).toContain('Pick up where you left off');
      await click('Continue');
      expect(router.url).toBe(`${PIPELINE}/context/${contextId}/prompt`);
    });

    it('stays exactly where it is when the move fails, and nothing opens as an empty run', async () => {
      await create({ unfiled: OLD, offline: true });

      expect(text()).toContain('still on this device');
      expect(root().querySelector('#cp-pipeline-step-heading')).toBeNull();
      expect(localStorage.getItem(UNFILED_KEY)).toBe(encodeContentPipelineDraft(OLD));
    });

    it('moves on a retry, making one context however many times it was tried', async () => {
      await create({ unfiled: OLD, offline: true });
      await click('Try again');
      expect(text()).toContain('still on this device');

      server.offline = false;
      await click('Try again');

      expect(server.creates.length).toBe(1);
      expect(router.url).toContain('/context/');
      expect(localStorage.getItem(UNFILED_KEY)).toBeNull();
      expect(text()).toContain('Step 3 of 6');
    });
  });

  describe('when the server cannot be reached', () => {
    it('keeps the edit on screen and on this device, and says it is not saved', async () => {
      await create({ run: filledDraft(), url: `${RUN}/setup` });
      server.offline = true;

      await typeConcept('Typed on a train.');
      await saved();

      expect(conceptValue()).toBe('Typed on a train.');
      expect(text()).toContain('not saved yet');
      expect(keptFor(CTX)?.unsent).toEqual({ pictureBrief: 'Typed on a train.' });
      expect(server.stored(SLUG, CTX)?.pictureBrief).toBe('A tight crop.');
    });

    it('sends it when the creator tries again', async () => {
      await create({ run: filledDraft(), url: `${RUN}/setup` });
      server.offline = true;
      await typeConcept('Typed on a train.');
      await saved();

      server.offline = false;
      await click('Try again');

      expect(server.stored(SLUG, CTX)?.pictureBrief).toBe('Typed on a train.');
      expect(text()).not.toContain('not saved yet');
      expect(keptFor(CTX)?.unsent).toBeNull();
    });

    it('brings unsent words back after the tab was closed, and sends them', async () => {
      await create({
        url: `${RUN}/setup`,
        setup: (fake) => {
          fake.seed(SLUG, CTX, { pictureBrief: 'A tight crop.' });
          localStorage.setItem(
            keptKey(CTX),
            encodeContentPipelineRun(emptyContentPipelineDraft(), { pictureBrief: 'Typed on a train.' }),
          );
        },
      });

      expect(conceptValue()).toBe('Typed on a train.');
      await saved();

      expect(server.stored(SLUG, CTX)?.pictureBrief).toBe('Typed on a train.');
    });

    it('keeps a first answer whole on this device when its context could not be made, and files it on a retry', async () => {
      await create({ offline: true });

      await typeConcept('A tight crop of the first slice.');

      expect(text()).toContain('not saved yet');
      expect(JSON.parse(localStorage.getItem(UNFILED_KEY)!).config.concept).toBe('A tight crop of the first slice.');

      server.offline = false;
      await click('Try again');

      expect(server.creates.length).toBe(1);
      expect(localStorage.getItem(UNFILED_KEY)).toBeNull();
      expect(server.stored(SLUG, session().context()!.id)?.pictureBrief).toBe('A tight crop of the first slice.');
    });
  });

  describe('when the context was changed somewhere else', () => {
    async function conflicted(): Promise<void> {
      await create({ run: filledDraft(), url: `${RUN}/setup` });
      server.changeElsewhere(SLUG, CTX, { pictureBrief: 'A wide shot of the table.' });

      await typeConcept('A tight crop, soft light.');
      await saved();
    }

    it('keeps the creator’s edit in the field, replaces nothing, and offers a reload', async () => {
      await conflicted();

      expect(conceptValue()).toBe('A tight crop, soft light.');
      expect(text()).toContain('changed somewhere else');
      expect(text()).toContain('the picture you have in mind');
      expect(button('Load the latest')).not.toBeNull();
      expect(button('Keep mine')).not.toBeNull();
      expect(server.stored(SLUG, CTX)?.pictureBrief).toBe('A wide shot of the table.');
      expect(root().querySelector('cp-creative-context-save-notice [role="alert"]')).not.toBeNull();
    });

    it('loads the latest only after asking, and keeps the edit when the answer is no', async () => {
      await conflicted();

      confirmAnswer = false;
      await click('Load the latest');
      expect(conceptValue()).toBe('A tight crop, soft light.');

      confirmAnswer = true;
      await click('Load the latest');
      expect(conceptValue()).toBe('A wide shot of the table.');
      expect(text()).not.toContain('changed somewhere else');
    });

    it('saves the creator’s edit over it when they keep theirs', async () => {
      await conflicted();

      await click('Keep mine');

      expect(server.stored(SLUG, CTX)?.pictureBrief).toBe('A tight crop, soft light.');
      expect(text()).not.toContain('changed somewhere else');
    });

    it('does not interrupt when what changed is something this screen had not touched', async () => {
      await create({ run: filledDraft(), url: `${RUN}/setup` });
      server.changeElsewhere(SLUG, CTX, { day: 'Friday' });

      await typeConcept('A tight crop, soft light.');
      await saved();

      expect(text()).not.toContain('changed somewhere else');
      expect(server.stored(SLUG, CTX)?.pictureBrief).toBe('A tight crop, soft light.');
      expect(server.stored(SLUG, CTX)?.day).toBe('Friday');
    });
  });

  describe('starting over', () => {
    it('asks first, and keeps everything when the answer is no', async () => {
      await create({ run: filledDraft({ furthestStep: 'idea' }), url: `${RUN}/idea` });

      confirmAnswer = false;
      await click('Start over');

      expect(router.url).toBe(`${RUN}/idea`);
      expect(keptFor(CTX)).not.toBeNull();
    });

    it('throws away what this device kept, leaves the context alone, and opens a clean run', async () => {
      const base = emptyContentPipelineDraft();
      await create({
        run: {
          ...base,
          config: { ...base.config, concept: 'A tight crop.', scene: ['marble slab'] },
          seed: { lastToken: 'abc-123', keep: {}, accepted: SEED },
          furthestStep: 'idea',
        },
        url: `${RUN}/idea`,
      });

      await click('Start over');

      expect(router.url).toBe(`${PIPELINE}/setup`);
      expect(localStorage.getItem(keptKey(CTX))).toBeNull();
      expect(localStorage.getItem(LAST_KEY)).toBeNull();
      expect(conceptValue()).toBe('');
      expect(root().querySelector('input[id^="cp-pipeline-scene-"]')).toBeNull();
      expect(text()).not.toContain('Pick up where you left off');
      expect(server.stored(SLUG, CTX)?.pictureBrief).withContext('nothing on the server is removed').toBe('A tight crop.');
    });
  });

  describe('two workspaces', () => {
    it('shows nothing of one workspace once the route names another', async () => {
      await create({ run: filledDraft({ furthestStep: 'idea' }), url: `${RUN}/idea` });

      expect(text()).toContain('Step 2 of 6');

      shell = await harness.navigateByUrl(`/${OTHER_SLUG}/workflows/content-pipeline/setup`, ContentPipelineShellComponent);
      await settle();

      // The other workspace has no run of its own, so neither the first one's progress nor its picture may
      // still be on screen.
      expect(text()).toContain('Step 1 of 6');
      expect(text()).not.toContain('Pick up where you left off');
      expect(conceptValue()).toBe('');
    });

    it("never offers one workspace's run in the other, and files the other's work under its own key and slug", async () => {
      await create({ run: filledDraft({ furthestStep: 'idea' }) });

      shell = await harness.navigateByUrl(`/${OTHER_SLUG}/workflows/content-pipeline/setup`, ContentPipelineShellComponent);
      await settle();
      await typeConcept('For the other kitchen.');
      await saved();

      expect(server.creates.length).toBe(1);
      expect(server.creates[0].slug).toBe(OTHER_SLUG);
      const otherId = session().context()!.id;
      expect(localStorage.getItem(`cp.pipeline.w2.m2.${otherId}`)).not.toBeNull();
      expect(localStorage.getItem('cp.pipeline.last.w2.m2')).toBe(otherId);
      expect(localStorage.getItem(LAST_KEY)).withContext("the first workspace's pointer is untouched").toBe(CTX);
      expect(server.stored(SLUG, CTX)?.pictureBrief).toBe('A tight crop.');
      expect(server.patches.every((patch) => patch.slug === OTHER_SLUG)).toBeTrue();
    });
  });

  it('waits for the memberships before judging the step in the URL against a run', async () => {
    // Memberships never resolve, so nothing is known about who is asking. The shell must not decide the step
    // from a default draft and send the creator back to the start of their own run.
    await create({ run: filledDraft({ furthestStep: 'idea' }), memberships: 'loading', url: `${RUN}/idea` });

    expect(text()).toContain('Loading your pipeline');
    expect(router.url).toBe(`${RUN}/idea`);
    expect(server.gets.length).toBe(0);
  });

  it('leaves without a guard, because leaving loses nothing', () => {
    const step = CONTENT_PIPELINE_ROUTES.find((route) => route.path === ':step');

    expect(step?.canDeactivate).toBeUndefined();
  });
});
