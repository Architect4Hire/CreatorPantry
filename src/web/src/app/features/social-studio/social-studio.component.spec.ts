import { Component, inject, signal } from '@angular/core';
import { By } from '@angular/platform-browser';
import { TestBed } from '@angular/core/testing';
import { Location } from '@angular/common';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { Observable, of } from 'rxjs';

import { ClipboardService } from '../../core/clipboard.service';
import { ConfirmService } from '../../core/confirm.service';
import { TextDownloadService } from '../../core/text-download.service';
import { WorkspaceRole } from '../../models/auth.models';
import { ContentChannel } from '../../models/brand-profile.models';
import {
  ChannelPostChannel,
  ChannelPostPackage,
  ChannelPostRevision,
  RequestChannelPostsRequest,
} from '../../models/channel-post.models';
import { CreativeContextListOutcome } from '../../services/creative-context.service';
import { DamAssetSummary } from '../../models/dam-asset.models';
import { WeeklyTheme } from '../../models/weekly-theme.models';
import { AiRequestOutcome, AiWatchOperationOutcome } from '../../services/ai-request';
import { AiAllowanceState, AiUsageService } from '../../services/ai-usage.service';
import { BrandProfileService } from '../../services/brand-profile.service';
import { ChannelPostPackageOutcome, ChannelPostService } from '../../services/channel-post.service';
import { CreativeContextSession } from '../../services/creative-context-session';
import { FakeCreativeContextService } from '../../services/creative-context.fake';
import { CreativeContextService } from '../../services/creative-context.service';
import { DamAssetService } from '../../services/dam-asset.service';
import { GeneratedImageService } from '../../services/generated-image.service';
import { FakeRecipeLibrary } from '../../services/recipe-library.fake';
import { RecipeService } from '../../services/recipe.service';
import { ReferenceImageService } from '../../services/reference-image.service';
import { SocialStudioDraftOwner, SocialStudioDraftService } from '../../services/social-studio-draft.service';
import { WeeklyThemesService } from '../../services/weekly-themes.service';
import { MyMembershipsState, WorkspaceMembershipService } from '../../services/workspace-membership.service';
import { ChannelPostRunState, emptyChannelPostRunState } from '../../shared/channel-posts/channel-post-run.component';
import { SocialStudioComponent } from './social-studio.component';
import { SocialStudioHost } from './social-studio-host';
import { WorkflowRunService } from '../../services/workflow-run.service';
import { WorkflowRun } from '../../models/workflow-run.models';
import { socialStudioCanDeactivateGuard } from './social-studio.guard';

const SLUG = 'cozy-fall';
const OTHER_SLUG = 'spring-table';
const OWNER: SocialStudioDraftOwner = { workspaceId: 'w-cozy', membershipId: 'm-cozy' };

const CHANNELS: readonly ContentChannel[] = [
  { key: 'instagram', displayName: 'Instagram', isActive: true },
  { key: 'pinterest', displayName: 'Pinterest', isActive: true },
];

const HEALTHY: AiAllowanceState = { kind: 'unknown' };

const SPENT: AiAllowanceState = {
  kind: 'exhausted',
  period: {
    unit: 'Credits',
    allowance: 1000,
    remaining: 0,
    consumed: 1000,
    carriedOver: 0,
    periodLength: 'Monthly',
    resetsAt: '2026-11-01T00:00:00Z',
    timeZoneId: 'Etc/UTC',
    usedPercent: 100,
  },
};

const THEMES: readonly WeeklyTheme[] = [
  {
    day: 'Monday',
    key: 'meat-free-monday',
    displayName: 'Meat-free Monday',
    description: null,
    standingNotes: 'Never tofu.',
    retiredAt: null,
    revision: 1,
  },
  {
    day: 'Friday',
    key: 'old-friday',
    displayName: 'Fish Friday',
    description: null,
    standingNotes: null,
    retiredAt: '2026-09-01T00:00:00Z',
    revision: 2,
  },
];

function revision(overrides: Partial<ChannelPostRevision> = {}): ChannelPostRevision {
  return {
    id: 'rev-1',
    revisionNumber: 1,
    source: 'AiGenerated',
    body: 'Olive oil cake, still warm.',
    characterCount: 26,
    characterLimit: 2200,
    limitStatus: 'Within',
    aiProposalId: 'p-1',
    createdAt: '2026-10-10T12:00:00Z',
    ...overrides,
  };
}

function channel(key: string, overrides: Partial<ChannelPostChannel> = {}): ChannelPostChannel {
  return {
    channelKey: key,
    status: 'Proposed',
    latest: revision({ id: `rev-${key}` }),
    accepted: null,
    isCurrent: null,
    updatedAt: '2026-10-10T12:00:00Z',
    ...overrides,
  };
}

function pkg(contextId: string, channels: readonly ChannelPostChannel[]): ChannelPostPackage {
  return { id: `pkg-${contextId}`, creativeContextId: contextId, channels, updatedAt: '2026-10-10T12:00:00Z' };
}

function asset(id: string, title: string): DamAssetSummary {
  return {
    id,
    title,
    description: null,
    kind: 'Photo',
    altText: null,
    channelKey: null,
    platformKey: null,
    day: null,
    styleKey: null,
    cuisineId: null,
    courseId: null,
    currentVersionNumber: 1,
    mediaType: 'image/jpeg',
    width: 100,
    height: 100,
    sizeBytes: 10,
    usageCount: null,
    createdAt: '2026-10-01T00:00:00Z',
    updatedAt: '2026-10-01T00:00:00Z',
  } as unknown as DamAssetSummary;
}

/** The context routes, plus the recent list — which the shared fake has no need of anywhere else. */
class FakeContexts extends FakeCreativeContextService {
  readonly lists: string[] = [];

  list(slug: string): Observable<CreativeContextListOutcome> {
    this.lists.push(slug);

    return of<CreativeContextListOutcome>({ status: 'found', page: { items: [], nextCursor: null } });
  }
}

/** The post routes, answering per piece of work and recording which workspace each read named. */
class FakePosts {
  packages: Record<string, ChannelPostPackage | null> = {};
  readonly reads: { slug: string; contextId: string }[] = [];
  readonly requests: { slug: string; request: RequestChannelPostsRequest }[] = [];

  readPackage(slug: string, contextId: string): Promise<ChannelPostPackageOutcome> {
    this.reads.push({ slug, contextId });

    return Promise.resolve({ status: 'found', package: this.packages[`${slug}/${contextId}`] ?? null });
  }

  request(slug: string, request: RequestChannelPostsRequest): Promise<AiRequestOutcome> {
    this.requests.push({ slug, request });

    return Promise.resolve({ status: 'unavailable' });
  }

  watch(): Observable<AiWatchOperationOutcome> {
    return of<AiWatchOperationOutcome>();
  }

  edit(): Promise<never> {
    return Promise.reject(new Error('not scripted'));
  }

  decide(): Promise<never> {
    return Promise.reject(new Error('not scripted'));
  }
}

let harness: RouterTestingHarness;
let server: FakeContexts;
let posts: FakePosts;
let library: FakeRecipeLibrary;
let kept: Record<string, ChannelPostRunState>;
let membershipSignal: ReturnType<typeof signal<MyMembershipsState>>;
let allowance: ReturnType<typeof signal<AiAllowanceState>>;
let channelsOutcome: { status: 'found'; channels: readonly ContentChannel[] } | { status: 'unavailable' };
let confirmCalls: string[];
let confirmAnswer: boolean;
let copied: string[];
let assets: readonly DamAssetSummary[];

const keptKey = (owner: SocialStudioDraftOwner, contextId: string): string =>
  `${owner.workspaceId}.${owner.membershipId}.${contextId}`;

function memberships(role: WorkspaceRole | null): MyMembershipsState {
  return {
    status: 'ready',
    memberships:
      role === null
        ? []
        : [
            {
              workspaceId: OWNER.workspaceId,
              workspaceSlug: SLUG,
              workspaceName: 'Cozy Fall',
              membershipId: OWNER.membershipId,
              role,
              status: 'Active',
            },
          ],
  };
}

const delay = (ms: number): Promise<void> => new Promise((resolve) => setTimeout(resolve, ms));

async function settle(): Promise<void> {
  for (let i = 0; i < 5; i += 1) {
    await delay(0);
    harness.detectChanges();
  }
}


/** How the next hosted studio's host is set, as a guided step sets it before the studio is made. */
let hostSetup: ((host: SocialStudioHost) => void) | null = null;
let hostedFilings: string[] = [];

@Component({
  imports: [SocialStudioComponent],
  providers: [SocialStudioHost],
  template: `<cp-social-studio />`,
})
class HostedStudioComponent {
  constructor() {
    const host = inject(SocialStudioHost);
    host.filed = (contextId) => hostedFilings.push(contextId);
    hostSetup?.(host);
  }
}

async function create(
  options: {
    role?: WorkspaceRole | null;
    url?: string;
    setup?: () => void;
  } = {},
): Promise<void> {
  server = new FakeContexts();
  posts = new FakePosts();
  library = new FakeRecipeLibrary([{ slug: SLUG, id: 'r-soda', title: 'Soda bread', versionIds: ['v-soda-1'] }]);
  kept = {};
  membershipSignal = signal(memberships(options.role === undefined ? 'Editor' : options.role));
  allowance = signal(HEALTHY);
  channelsOutcome = { status: 'found', channels: CHANNELS };
  confirmCalls = [];
  confirmAnswer = true;
  copied = [];
  assets = [asset('a-loaf', 'The loaf, torn open')];
  options.setup?.();

  const neverAnswers = (): Observable<AiWatchOperationOutcome> => of<AiWatchOperationOutcome>();

  TestBed.resetTestingModule();
  await TestBed.configureTestingModule({
    providers: [
      provideRouter([
        { path: ':workspaceSlug/social-studio', component: SocialStudioComponent, canDeactivate: [socialStudioCanDeactivateGuard] },
        {
          path: ':workspaceSlug/social-studio/context/:contextId',
          component: SocialStudioComponent,
          canDeactivate: [socialStudioCanDeactivateGuard],
        },
        { path: ':workspaceSlug/dashboard', children: [] },
        { path: ':workspaceSlug/workflows/runs/:runId', component: HostedStudioComponent },
      ]),
      {
        provide: WorkflowRunService,
        useValue: {
          get: () => Promise.resolve({ status: 'found' as const, run: { goal: 'Create social posts' } as unknown as WorkflowRun }),
        },
      },
      { provide: CreativeContextService, useValue: server },
      { provide: ChannelPostService, useValue: posts },
      { provide: RecipeService, useValue: library },
      {
        provide: SocialStudioDraftService,
        useValue: {
          readKept: (owner: SocialStudioDraftOwner, contextId: string) => ({
            kept: kept[keptKey(owner, contextId)] ?? null,
            discarded: false,
          }),
          writeKept: (owner: SocialStudioDraftOwner, contextId: string, state: ChannelPostRunState) => {
            kept[keptKey(owner, contextId)] = state;

            return true;
          },
          clearKept: (owner: SocialStudioDraftOwner, contextId: string) => {
            delete kept[keptKey(owner, contextId)];
          },
        },
      },
      {
        provide: WorkspaceMembershipService,
        useValue: { state: membershipSignal, ensureLoaded: () => Promise.resolve(), load: () => Promise.resolve() },
      },
      {
        provide: BrandProfileService,
        useValue: {
          listContentChannels: () => Promise.resolve(channelsOutcome),
          getBrandProfile: () => Promise.resolve({ status: 'not_found' as const }),
        },
      },
      {
        provide: WeeklyThemesService,
        useValue: { getWeek: () => Promise.resolve({ status: 'found' as const, themes: THEMES }) },
      },
      {
        provide: DamAssetService,
        useValue: {
          search: () => of({ status: 'found' as const, page: { items: assets, nextCursor: null, totalCount: assets.length } }),
          detail: () => of({ status: 'unavailable' as const }),
          content: () => of({ status: 'unavailable' as const }),
          versionContent: () => of({ status: 'unavailable' as const }),
        },
      },
      { provide: GeneratedImageService, useValue: { preview: () => of({ status: 'unavailable' as const }) } },
      {
        provide: ReferenceImageService,
        useValue: { request: () => Promise.resolve({ status: 'unavailable' as const }), watch: neverAnswers },
      },
      { provide: AiUsageService, useValue: { allowance, ensureLoaded: () => Promise.resolve() } },
      {
        provide: ConfirmService,
        useValue: {
          confirm: (request: { title: string }) => {
            confirmCalls.push(request.title);

            return Promise.resolve(confirmAnswer);
          },
        },
      },
      {
        provide: ClipboardService,
        useValue: {
          copy: (text: string) => {
            copied.push(text);

            return Promise.resolve(true);
          },
        },
      },
      { provide: TextDownloadService, useValue: { save: () => true } },
    ],
  }).compileComponents();

  harness = await RouterTestingHarness.create();
  if (options.url?.includes('/workflows/runs/')) await harness.navigateByUrl(options.url);
  else await harness.navigateByUrl(options.url ?? `/${SLUG}/social-studio`, SocialStudioComponent);
  await settle();
}

/** The studio a guided step hosts, showing one part (13A.20). */
async function hosted(setupHost: (host: SocialStudioHost) => void, setup?: () => void): Promise<void> {
  hostSetup = setupHost;
  hostedFilings = [];
  await create({ url: `/${SLUG}/workflows/runs/run-1`, setup });
}

function hostedStudio(): SocialStudioComponent {
  return harness.routeDebugElement!.query(By.directive(SocialStudioComponent)).componentInstance;
}

function hostedSession(): CreativeContextSession {
  return harness.routeDebugElement!.query(By.directive(SocialStudioComponent)).injector.get(CreativeContextSession);
}

function root(): HTMLElement {
  return harness.routeNativeElement as HTMLElement;
}

function text(): string {
  return root().textContent ?? '';
}

function session(): CreativeContextSession {
  return harness.routeDebugElement!.injector.get(CreativeContextSession);
}

function button(label: string, within: ParentNode = root()): HTMLButtonElement | null {
  return (
    Array.from(within.querySelectorAll<HTMLButtonElement>('button')).find(
      (node) => node.textContent?.trim() === label,
    ) ?? null
  );
}

async function click(label: string): Promise<void> {
  button(label)!.click();
  await settle();
}

async function type(selector: string, value: string): Promise<void> {
  const field = root().querySelector<HTMLInputElement | HTMLTextAreaElement>(selector)!;
  field.value = value;
  field.dispatchEvent(new Event('input'));
  await settle();
}

/** Send whatever the autosave is waiting to send, without waiting out its delay. */
async function saved(): Promise<void> {
  await session().flush();
  await settle();
}

describe('SocialStudioComponent', () => {
  // ---- first use -------------------------------------------------------------------------------------

  it('opens with the three things posts can be about, and leaves nothing behind by arriving', async () => {
    await create();

    expect(root().querySelector('cp-page-head')?.textContent).toContain('Social Studio');
    expect(root().querySelector('cp-recipe-picker')).not.toBeNull();
    expect(button('Choose a picture')).not.toBeNull();
    expect(root().querySelector('#cp-social-brief')).not.toBeNull();

    // Nothing to write for yet, and the run says how to get there in this page's own words.
    expect(text()).toContain('Link a recipe, choose a picture or write a few lines above');
    expect(root().querySelector('input[type="checkbox"]')).toBeNull();

    expect(server.creates.length).toBe(0);
    expect(posts.reads.length).toBe(0);
  });

  it('does not offer a saved prompt as something posts can be about', async () => {
    await create();

    expect(text().toLowerCase()).not.toContain('choose a prompt');
    expect(root().querySelector('[id*="prompt"]')).toBeNull();
  });

  // ---- starting from a typed brief -------------------------------------------------------------------

  it('files the work on a creative context on the first answer, and keeps a typed brief only as its brief', async () => {
    await create();

    await type('#cp-social-brief', 'My cookbook is out on Friday.');
    await saved();

    // One context, made bare: the create route takes no brief, so it follows as an ordinary edit.
    expect(server.creates.length).toBe(1);
    expect(server.creates[0].slug).toBe(SLUG);
    expect(server.creates[0].draft.from).toBeUndefined();
    expect(server.patches.at(-1)?.patch.workingBrief).toBe('My cookbook is out on Friday.');

    // Untrusted typed text is this piece of work's brief and nothing else: no source was made out of it.
    expect(server.added.length).toBe(0);
    expect(server.stored(SLUG, 'ctx-1')?.pictureBrief).toBeNull();
    expect(server.stored(SLUG, 'ctx-1')?.references).toEqual([]);

    // The address is now the one that finds this work again, and the channels are offered.
    expect(TestBed.inject(Location).path()).toBe(`/${SLUG}/social-studio/context/ctx-1`);
    expect(root().querySelectorAll('input[type="checkbox"]').length).toBe(2);
  });

  it('asks for posts about that piece of work, in this workspace, on the channels ticked', async () => {
    await create();
    await type('#cp-social-title', 'Cookbook launch');
    await saved();

    root().querySelector<HTMLInputElement>('input[type="checkbox"][value="instagram"]')!.click();
    await settle();
    await click('Write the post');

    expect(posts.requests).toEqual([
      { slug: SLUG, request: { creativeContextId: 'ctx-1', channelKeys: ['instagram'] } },
    ]);
  });

  // ---- starting from a recipe or a picture -----------------------------------------------------------

  it('names a chosen library picture on the work as something it draws on, and copies nothing', async () => {
    await create();

    await click('Choose a picture');
    root().querySelector<HTMLButtonElement>('[data-choose="a-loaf"]')!.click();
    await settle();

    expect(server.added.at(-1)?.source).toEqual({ kind: 'DamAsset', mediaAssetId: 'a-loaf' });
    expect(text()).toContain('A picture goes with these posts.');
    expect(button('Choose a different picture')).not.toBeNull();

    await click('Remove the picture');

    expect(server.removed.length).toBe(1);
    expect(button('Choose a picture')).not.toBeNull();
  });

  it('says so when a picture cannot be added, and leaves the work as it was', async () => {
    await create();
    await type('#cp-social-title', 'Loaf');
    await saved();
    server.refuseAddWith = 'source_unavailable';

    await click('Choose a picture');
    root().querySelector<HTMLButtonElement>('[data-choose="a-loaf"]')!.click();
    await settle();

    expect(root().querySelector('[role="alert"]')?.textContent).toContain('can no longer be used');
    expect(server.stored(SLUG, 'ctx-1')?.references).toEqual([]);
  });

  // ---- a themed day ----------------------------------------------------------------------------------

  it('offers the live themed days and stores a day and its theme together', async () => {
    await create();

    const select = root().querySelector<HTMLSelectElement>('#cp-social-theme')!;
    const offered = Array.from(select.options).map((option) => option.textContent?.trim());

    // A retired theme is not offered to work that was not made for it.
    expect(offered).toEqual(['No themed day', 'Monday — Meat-free Monday']);

    select.value = 'meat-free-monday';
    select.dispatchEvent(new Event('change'));
    await settle();
    await saved();

    expect(server.creates[0].draft.day).toBe('Monday');
    expect(server.creates[0].draft.weeklyThemeKey).toBe('meat-free-monday');
    // The day's notes are in effect, and said before any post is asked for.
    expect(text()).toContain('standing notes are in effect');
  });

  // ---- reopened --------------------------------------------------------------------------------------

  describe('opened on an existing piece of work', () => {
    const open = (role: WorkspaceRole = 'Editor', setup: () => void = () => undefined): Promise<void> =>
      create({
        role,
        url: `/${SLUG}/social-studio/context/ctx-have`,
        setup: () => {
          server.seed(SLUG, 'ctx-have', { workingTitle: 'Olive oil cake', workingBrief: 'Warm, with coffee.' });
          posts.packages[`${SLUG}/ctx-have`] = pkg('ctx-have', [channel('instagram'), channel('pinterest')]);
          setup();
        },
      });

    it('reads what the posts are about and the posts themselves, and creates nothing', async () => {
      await open();

      expect(root().querySelector<HTMLInputElement>('#cp-social-title')!.value).toBe('Olive oil cake');
      expect(root().querySelector<HTMLTextAreaElement>('#cp-social-brief')!.value).toBe('Warm, with coffee.');
      expect(root().querySelectorAll('cp-channel-post-review').length).toBe(2);
      expect(text()).toContain('Written for you');
      expect(server.creates.length).toBe(0);
    });

    it('shows nothing as accepted that the creator has not accepted', async () => {
      await open();

      expect(text()).not.toContain('Accepted');
      expect(Array.from(root().querySelectorAll('cp-status-pill')).some((pill) => pill.textContent?.includes('Waiting for you'))).toBeTrue();
    });

    it('brings back the words of a post edited and not saved on this device', async () => {
      await open('Editor', () => {
        kept[keptKey(OWNER, 'ctx-have')] = { ...emptyChannelPostRunState(), unsaved: { instagram: 'My own caption.' } };
      });

      expect(root().querySelector<HTMLTextAreaElement>('textarea[id$="instagram-body"]')!.value).toBe('My own caption.');
    });

    it('keeps an unsaved edit on this device as it is typed, under this member and this piece of work', async () => {
      await open();

      button('Edit', root().querySelector('cp-channel-post-review')!)!.click();
      await settle();
      await type('textarea[id$="instagram-body"]', 'Typed and not saved.');

      expect(kept[keptKey(OWNER, 'ctx-have')]?.unsaved).toEqual({ instagram: 'Typed and not saved.' });
    });

    it('asks before leaving with unsaved words, saying where they are, and stays when told to', async () => {
      await open('Editor', () => {
        kept[keptKey(OWNER, 'ctx-have')] = { ...emptyChannelPostRunState(), unsaved: { instagram: 'My own caption.' } };
      });
      confirmAnswer = false;

      await harness.navigateByUrl(`/${SLUG}/dashboard`);
      await settle();

      expect(confirmCalls).toEqual(['Leave with unsaved words?']);
      expect(TestBed.inject(Location).path()).toBe(`/${SLUG}/social-studio/context/ctx-have`);
      // Still there, whichever way the question was answered.
      expect(kept[keptKey(OWNER, 'ctx-have')]?.unsaved).toEqual({ instagram: 'My own caption.' });
    });

    it('leaves without asking when nothing is unsaved', async () => {
      await open();

      await harness.navigateByUrl(`/${SLUG}/dashboard`);

      expect(confirmCalls).toEqual([]);
      expect(TestBed.inject(Location).path()).toBe(`/${SLUG}/dashboard`);
    });

    it('starts something new on the same screen, leaving the piece of work as it was', async () => {
      await open();

      await click('Start something new');

      expect(root().querySelector<HTMLInputElement>('#cp-social-title')!.value).toBe('');
      expect(root().querySelectorAll('cp-channel-post-review').length).toBe(0);
      expect(TestBed.inject(Location).path()).toBe(`/${SLUG}/social-studio`);
      expect(server.stored(SLUG, 'ctx-have')?.workingTitle).toBe('Olive oil cake');
      expect(server.patches.length).toBe(0);
      // The button pressed has gone with the work it left; focus is on the page's title, not dropped.
      expect(document.activeElement).toBe(root().querySelector('h1'));
    });

    it('says plainly that a saved prompt this work names is not what posts are written from', async () => {
      await open('Editor', () => {
        const context = server.seed(SLUG, 'ctx-have', { workingTitle: 'From a prompt' });
        server.seed(SLUG, 'ctx-have', {
          ...context,
          references: [server.reference({ kind: 'PromptRecord', promptRecordId: 'pr-1' }, 0)],
        });
      });

      expect(text()).toContain('Posts are not written from one');
    });

    it('holds asking for more while the allowance will not cover it, and says why', async () => {
      await open();
      allowance.set(SPENT);
      await settle();

      expect(root().querySelector('cp-ai-allowance-notice')?.textContent?.trim()).not.toBe('');
      expect(button('Write it again', root().querySelector('cp-channel-post-review')!)!.disabled).toBeTrue();
    });
  });

  // ---- workspace isolation ---------------------------------------------------------------------------

  it('opens a context id from another workspace as not found, and reads none of its posts', async () => {
    await create({
      url: `/${SLUG}/social-studio/context/ctx-theirs`,
      setup: () => {
        // The other workspace's piece of work and its posts: real, and not this workspace's.
        server.seed(OTHER_SLUG, 'ctx-theirs', { workingTitle: 'Their spring menu', workingBrief: 'Their words.' });
        posts.packages[`${OTHER_SLUG}/ctx-theirs`] = pkg('ctx-theirs', [
          channel('instagram', { latest: revision({ body: 'Their private caption.' }) }),
        ]);
      },
    });

    expect(root().querySelector('[role="alert"]')?.textContent).toContain("couldn't find this piece of work");
    expect(text()).not.toContain('Their spring menu');
    expect(text()).not.toContain('Their private caption.');
    expect(root().querySelector('cp-channel-post-run')).toBeNull();

    // Asked for under this workspace's slug and no other, and no post read was made for it at all.
    expect(server.gets).toEqual([{ slug: SLUG, id: 'ctx-theirs' }]);
    expect(posts.reads.some((read) => read.contextId === 'ctx-theirs')).toBeFalse();
    expect(posts.reads.every((read) => read.slug === SLUG)).toBeTrue();
  });

  it('does not open a workspace the creator is not a member of', async () => {
    await create({ role: null });

    expect(root().querySelector('[role="alert"]')?.textContent).toContain("couldn't find this workspace");
    expect(server.gets.length).toBe(0);
    expect(server.lists.length).toBe(0);
  });

  // ---- view-only -------------------------------------------------------------------------------------

  describe('for a Viewer', () => {
    it('lets them read and copy an existing set, and offers nothing that writes', async () => {
      await create({
        role: 'Viewer',
        url: `/${SLUG}/social-studio/context/ctx-have`,
        setup: () => {
          server.seed(SLUG, 'ctx-have', { workingTitle: 'Olive oil cake' });
          posts.packages[`${SLUG}/ctx-have`] = pkg('ctx-have', [channel('instagram')]);
        },
      });

      expect(text()).toContain('Olive oil cake, still warm.');
      expect(text()).toContain('you can read and copy these posts');

      await click('Copy');
      expect(copied).toEqual(['Olive oil cake, still warm.']);

      expect(root().querySelector('#cp-social-title')).toBeNull();
      expect(root().querySelector('cp-recipe-picker')).toBeNull();
      for (const label of ['Accept', 'Edit', 'Write it again', 'Choose a picture', 'Start something new']) {
        expect(button(label)).withContext(label).toBeNull();
      }
      expect(server.patches.length + server.creates.length + server.added.length).toBe(0);
    });

    it('says they cannot start posts, and files nothing, at the bare address', async () => {
      await create({ role: 'Viewer' });

      expect(text()).toContain("can't start new posts here");
      expect(root().querySelector('cp-channel-post-run')).toBeNull();
      expect(root().querySelector('cp-social-studio-recent')).not.toBeNull();
      expect(server.creates.length).toBe(0);
    });
  });

  // ---- degraded and failed reads ---------------------------------------------------------------------

  it('says the channel list could not be loaded and still shows the posts already written', async () => {
    await create({
      url: `/${SLUG}/social-studio/context/ctx-have`,
      setup: () => {
        channelsOutcome = { status: 'unavailable' };
        server.seed(SLUG, 'ctx-have', { workingTitle: 'Olive oil cake' });
        posts.packages[`${SLUG}/ctx-have`] = pkg('ctx-have', [channel('instagram')]);
      },
    });

    expect(text()).toContain("list of channels couldn't be loaded");
    expect(text()).toContain('Olive oil cake, still warm.');
  });

  it('offers another try when the piece of work could not be read, rather than an empty studio', async () => {
    await create({
      url: `/${SLUG}/social-studio/context/ctx-have`,
      setup: () => {
        server.seed(SLUG, 'ctx-have', { workingTitle: 'Olive oil cake' });
        server.offline = true;
      },
    });

    expect(root().querySelector('[role="alert"]')?.textContent).toContain("couldn't be loaded right now");
    expect(root().querySelector('#cp-social-title')).toBeNull();

    server.offline = false;
    await click('Try again');

    expect(root().querySelector<HTMLInputElement>('#cp-social-title')!.value).toBe('Olive oil cake');
  });

  // ---- accessibility ---------------------------------------------------------------------------------

  it('has one h1, labels every field, and has a polite region for what it announces', async () => {
    await create();

    expect(root().querySelectorAll('h1').length).toBe(1);

    for (const id of ['cp-social-title', 'cp-social-brief', 'cp-social-theme']) {
      expect(root().querySelector(`label[for="${id}"]`)).withContext(id).not.toBeNull();
    }

    const live = root().querySelector('.cp-sr-only[role="status"]');
    expect(live?.getAttribute('aria-live')).toBe('polite');
  });

  describe('hosted in a guided step (13A.20)', () => {
    afterEach(() => (hostSetup = null));

    it('asks only what the posts are about on the first part, offering the run’s recipe', async () => {
      await hosted((host) => {
        host.part = 'about';
        host.suggestedRecipeId = 'r-soda';
      });

      expect(root().querySelector('cp-page-head')).withContext('the run page owns the heading').toBeNull();
      expect(button('Use “Soda bread”')).not.toBeNull();
      expect(root().querySelector('#cp-social-brief')).not.toBeNull();
      expect(root().querySelector('#cp-social-theme, #cp-social-day')).not.toBeNull();
      expect(button('Choose a picture')).withContext('the picture is a step of its own').toBeNull();
      expect(root().querySelector('cp-channel-post-run')).toBeNull();
      expect(root().querySelector('cp-social-studio-recent')).toBeNull();
      expect(root().querySelector('.legend')).withContext('the step’s card carries the shell’s legend').toBeNull();
    });

    it('files new work on its first answer for the run, and leaves the address alone', async () => {
      await hosted((host) => (host.part = 'about'));
      const location = TestBed.inject(Location);
      const before = location.path();

      await type('#cp-social-brief', 'My cookbook is out on Friday.');
      await hostedSession().flush();
      await settle();

      expect(server.creates.length).toBe(1);
      expect(hostedFilings.length).withContext('the run is told, once').toBe(1);
      expect(hostedStudio().workContext()?.id).toBe(hostedFilings[0]);
      expect(location.path()).withContext('the address is the run’s').toBe(before);
    });

    it('writes and reviews posts on the second part, and says nothing is kept until one is', async () => {
      const id = 'ctx-posts';
      await hosted(
        (host) => {
          host.part = 'write';
          host.contextId = id;
        },
        () => {
          server.seed(SLUG, id, { workingTitle: 'Soda bread' });
          posts.packages[`${SLUG}/${id}`] = pkg(id, [channel('instagram', { status: 'Proposed' })]);
        },
      );

      expect(root().querySelector('cp-channel-post-run')).not.toBeNull();
      expect(root().querySelector('cp-recipe-picker')).toBeNull();
      expect(hostedStudio().anyPostKept()).toBeFalse();
    });

    it('counts a post as kept only when the server says one is accepted', async () => {
      const id = 'ctx-posts';
      await hosted(
        (host) => {
          host.part = 'write';
          host.contextId = id;
        },
        () => {
          server.seed(SLUG, id, { workingTitle: 'Soda bread' });
          posts.packages[`${SLUG}/${id}`] = pkg(id, [channel('instagram', { status: 'Accepted' })]);
        },
      );

      expect(hostedStudio().anyPostKept()).toBeTrue();
    });

    it('says where to go when the second part has nothing to write about', async () => {
      await hosted((host) => (host.part = 'write'));

      expect(text()).toContain('Go back to the first step to link a recipe or write a few lines');
    });

    it('chooses a picture on the third part, and nothing else', async () => {
      const id = 'ctx-posts';
      await hosted(
        (host) => {
          host.part = 'picture';
          host.contextId = id;
        },
        () => server.seed(SLUG, id, { workingTitle: 'Soda bread' }),
      );

      expect(button('Choose a picture')).not.toBeNull();
      expect(root().querySelector('#cp-social-brief')).toBeNull();
      expect(root().querySelector('cp-channel-post-run')).toBeNull();
    });
  });

  describe('opened from a guided run (13A.20)', () => {
    it('says which workflow sent the creator here, with the way back', async () => {
      await create({ url: `/${SLUG}/social-studio?workflowRun=0f6f2a54-7c1e-4d4b-9a51-2f1b3c4d5e6f` });

      const notice = root().querySelector('cp-workflow-return-notice');
      expect(notice?.querySelector('a')?.getAttribute('href')).toBe(`/${SLUG}/workflows/runs/0f6f2a54-7c1e-4d4b-9a51-2f1b3c4d5e6f`);
    });
  });
});
