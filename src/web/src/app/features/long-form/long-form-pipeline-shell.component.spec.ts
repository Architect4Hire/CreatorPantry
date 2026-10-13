import { Component, forwardRef, input, output, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';

import { ConfirmService } from '../../core/confirm.service';
import { AiProposalStatus } from '../../models/ai-proposal.models';
import { WorkspaceRole } from '../../models/auth.models';
import { BrandProfile } from '../../models/brand-profile.models';
import { ChannelPostPackage } from '../../models/channel-post.models';
import { LongFormAssembledDocument } from '../../models/long-form-document.models';
import { LongFormDraft, LongFormDraftStatus, StartLongFormDraftRequest } from '../../models/long-form-draft.models';
import { decodeLongFormPipelinePlace, encodeLongFormPipelinePlace } from '../../models/long-form-pipeline.models';
import { AiAllowanceState, AiUsageService } from '../../services/ai-usage.service';
import { AuthService, SessionState } from '../../services/auth.service';
import { BrandProfileOutcome, BrandProfileService } from '../../services/brand-profile.service';
import { BrandVisualGuideService } from '../../services/brand-visual-guide.service';
import { ChannelPostService } from '../../services/channel-post.service';
import { CreativeContextSession } from '../../services/creative-context-session';
import { FakeCreativeContextService } from '../../services/creative-context.fake';
import { CreativeContextService } from '../../services/creative-context.service';
import {
  LongFormDraftOutcome,
  LongFormDraftPageOutcome,
  LongFormDraftService,
  LongFormStartOutcome,
} from '../../services/long-form-draft.service';
import { FakeRecipeLibrary } from '../../services/recipe-library.fake';
import { RecipeService } from '../../services/recipe.service';
import { MyMembershipsState, WorkspaceMembershipService } from '../../services/workspace-membership.service';
import { LongFormDocumentReviewComponent } from './long-form-document-review.component';
import { LongFormPipelineShellComponent, LONG_FORM_PIPELINE_HEADING_ID } from './long-form-pipeline-shell.component';
import { LONG_FORM_PIPELINE_ROUTES, longFormPipelineMatcher } from './long-form-pipeline.routes';
import { LongFormPlanReviewComponent } from './long-form-plan-review.component';

/**
 * The two surfaces the shell composes, stood in for: each has its own specs, and what is under test here is
 * what the shell gives them, what it does when they report, and that it asks them before leaving. Provided
 * under the real class so the shell's own `viewChild` finds them.
 */
@Component({
  selector: 'cp-long-form-plan-review',
  template: '<p data-testid="plan-surface">plan</p>',
  providers: [{ provide: LongFormPlanReviewComponent, useExisting: forwardRef(() => StubPlanReview) }],
})
class StubPlanReview {
  readonly workspaceSlug = input.required<string>();
  readonly draftId = input.required<string>();
  readonly outlineRequestId = input<string | null>(null);
  readonly allowance = input.required<AiAllowanceState>();
  readonly approved = output<LongFormDraft>();

  confirmDiscardIfDirty(): Promise<boolean> {
    leaveAsked += 1;

    return Promise.resolve(leaveAnswer);
  }
}

@Component({
  selector: 'cp-long-form-document-review',
  template: '<p data-testid="document-surface">document</p>',
  providers: [{ provide: LongFormDocumentReviewComponent, useExisting: forwardRef(() => StubDocumentReview) }],
})
class StubDocumentReview {
  readonly workspaceSlug = input.required<string>();
  readonly draftId = input.required<string>();
  readonly allowance = input.required<AiAllowanceState>();
  readonly show = input<'all' | 'write' | 'accept'>('all');
  readonly accepted = output<LongFormAssembledDocument>();
  readonly documentRead = output<LongFormAssembledDocument>();

  confirmDiscardIfDirty(): Promise<boolean> {
    leaveAsked += 1;

    return Promise.resolve(leaveAnswer);
  }
}

@Component({ selector: 'cp-test-elsewhere', template: '<p>elsewhere</p>' })
class ElsewhereComponent {}

const SLUG = 'cozy-fall';
const OTHER_SLUG = 'other-kitchen';
const BASE = `/${SLUG}/workflows/long-form`;
const PLACE_KEY = 'cp.longform.w1.m1';
const delay = (ms: number): Promise<void> => new Promise((resolve) => setTimeout(resolve, ms));

let leaveAnswer = true;
let leaveAsked = 0;
let confirmAnswer = true;

function draftOf(id: string, contextId: string, status: LongFormDraftStatus, overrides: Partial<LongFormDraft> = {}): LongFormDraft {
  return {
    id,
    creativeContextId: contextId,
    kind: 'BlogArticle',
    status,
    concurrencyToken: 'token-1',
    createdAt: '2026-10-12T09:00:00Z',
    updatedAt: '2026-10-12T09:00:00Z',
    latest: null,
    planCheck: null,
    framing: null,
    unresolvedItems: [],
    ...overrides,
  };
}

function operation(): AiProposalStatus {
  return {
    aiProposalRequestId: 'req-outline',
    status: 'Requested',
    taskType: 'LongFormOutline',
    scope: 'NotApplicable',
    sourceVersionId: null,
    requestedAt: '2026-10-12T09:00:00Z',
    statusChangedAt: '2026-10-12T09:00:00Z',
    failureCategory: null,
    proposal: null,
  };
}

/** The draft seam, scripted. A draft belongs to one workspace, and is not there for another. */
class FakeLongFormDraftService {
  readonly drafts = new Map<string, { slug: string; draft: LongFormDraft }>();
  readonly starts: { slug: string; request: StartLongFormDraftRequest; key: string }[] = [];
  startOutcomes: LongFormStartOutcome[] = [];
  unavailable = false;

  hold(slug: string, draft: LongFormDraft): void {
    this.drafts.set(draft.id, { slug, draft });
  }

  start(slug: string, request: StartLongFormDraftRequest, key: string): Promise<LongFormStartOutcome> {
    this.starts.push({ slug, request, key });

    const scripted = this.startOutcomes.shift();
    if (scripted !== undefined) return Promise.resolve(scripted);

    const draft = draftOf('d-new', request.creativeContextId, 'Draft', { kind: request.kind });
    this.hold(slug, draft);

    return Promise.resolve({ status: 'accepted', draft, operation: operation(), replayed: false });
  }

  read(slug: string, draftId: string): Promise<LongFormDraftOutcome> {
    if (this.unavailable) return Promise.resolve({ status: 'unavailable' });

    const held = this.drafts.get(draftId);

    return Promise.resolve(held !== undefined && held.slug === slug ? { status: 'found', draft: held.draft } : { status: 'not_found' });
  }

  list(slug: string, query: { creativeContextId?: string | null }): Promise<LongFormDraftPageOutcome> {
    const items = [...this.drafts.values()]
      .filter((held) => held.slug === slug && held.draft.creativeContextId === query.creativeContextId)
      .map(({ draft }) => ({
        id: draft.id,
        creativeContextId: draft.creativeContextId,
        kind: draft.kind,
        status: draft.status,
        latestRevisionNumber: null,
        updatedAt: draft.updatedAt,
      }));

    return Promise.resolve({ status: 'found', page: { items, nextCursor: null } });
  }
}

let harness: RouterTestingHarness;
let router: Router;
let server: FakeCreativeContextService;
let drafts: FakeLongFormDraftService;
let library: FakeRecipeLibrary;
let posts: ChannelPostPackage | null;
let allowance: ReturnType<typeof signal<AiAllowanceState>>;

function memberships(role: WorkspaceRole | null, status: 'ready' | 'loading' | 'error') {
  if (status !== 'ready') return signal<MyMembershipsState>({ status });

  return signal<MyMembershipsState>({
    status: 'ready',
    memberships:
      role === null
        ? []
        : [
            { workspaceId: 'w1', workspaceSlug: SLUG, workspaceName: 'Cozy Fall', membershipId: 'm1', role, status: 'Active' as const },
            { workspaceId: 'w2', workspaceSlug: OTHER_SLUG, workspaceName: 'Other Kitchen', membershipId: 'm2', role, status: 'Active' as const },
          ],
  });
}

async function create(
  options: {
    url?: string;
    role?: WorkspaceRole | null;
    memberships?: 'ready' | 'loading' | 'error';
    brand?: BrandProfileOutcome;
    guide?: { name: string; versionNumber: number } | null | 'unavailable';
    setup?: () => void;
  } = {},
): Promise<void> {
  leaveAnswer = true;
  leaveAsked = 0;
  confirmAnswer = true;
  options.setup?.();

  TestBed.resetTestingModule();
  TestBed.overrideComponent(LongFormPipelineShellComponent, {
    remove: { imports: [LongFormPlanReviewComponent, LongFormDocumentReviewComponent] },
    add: { imports: [StubPlanReview, StubDocumentReview] },
  });

  await TestBed.configureTestingModule({
    providers: [
      provideRouter([
        { path: ':workspaceSlug/workflows/long-form', children: LONG_FORM_PIPELINE_ROUTES },
        { path: ':workspaceSlug/workflows', component: ElsewhereComponent },
      ]),
      // The real device store over the real localStorage: what is kept on the device is what several of these
      // specs are about.
      { provide: AuthService, useValue: { session: signal<SessionState>({ status: 'authenticated', displayName: 'Rae' }).asReadonly() } },
      { provide: CreativeContextService, useValue: server },
      { provide: RecipeService, useValue: library },
      { provide: LongFormDraftService, useValue: drafts },
      {
        provide: WorkspaceMembershipService,
        useValue: {
          state: memberships(options.role === undefined ? 'Editor' : options.role, options.memberships ?? 'ready'),
          ensureLoaded: () => Promise.resolve(),
          load: () => Promise.resolve(),
        },
      },
      {
        provide: BrandProfileService,
        useValue: {
          listContentChannels: () =>
            Promise.resolve({ status: 'found', channels: [{ key: 'instagram', displayName: 'Instagram', isActive: true }] }),
          getBrandProfile: () => Promise.resolve(options.brand ?? { status: 'first_use' }),
        },
      },
      {
        provide: BrandVisualGuideService,
        useValue: {
          get: () =>
            Promise.resolve(
              options.guide === 'unavailable'
                ? { status: 'unavailable' }
                : { status: 'found', guide: { activeGuide: options.guide ?? null } },
            ),
        },
      },
      { provide: ChannelPostService, useValue: { readPackage: () => Promise.resolve({ status: 'found', package: posts }) } },
      { provide: ConfirmService, useValue: { confirm: () => Promise.resolve(confirmAnswer) } },
      { provide: AiUsageService, useValue: { allowance, ensureLoaded: () => Promise.resolve() } },
    ],
  }).compileComponents();

  router = TestBed.inject(Router);
  harness = await RouterTestingHarness.create();
  await harness.navigateByUrl(options.url ?? BASE);
  await settle();
}

async function settle(): Promise<void> {
  for (let i = 0; i < 6; i += 1) {
    await delay(0);
    harness.detectChanges();
  }
}

function root(): HTMLElement {
  return harness.routeNativeElement as HTMLElement;
}

function text(node: ParentNode = root()): string {
  return (node as HTMLElement).textContent?.replace(/\s+/g, ' ').trim() ?? '';
}

function button(label: string): HTMLButtonElement | null {
  return Array.from(root().querySelectorAll<HTMLButtonElement>('button')).find((candidate) => text(candidate) === label) ?? null;
}

async function click(label: string): Promise<void> {
  const target = button(label);
  if (target === null) throw new Error(`No button "${label}". There are: ${Array.from(root().querySelectorAll('button')).map((b) => text(b)).join(' | ')}`);

  target.click();
  await settle();
}

async function type(id: string, value: string): Promise<void> {
  const field = root().querySelector<HTMLInputElement | HTMLTextAreaElement>(`#${id}`)!;
  field.value = value;
  field.dispatchEvent(new Event('input'));
  await settle();
}

function session(): CreativeContextSession {
  return harness.routeDebugElement!.injector.get(CreativeContextSession);
}

function shell(): LongFormPipelineShellComponent {
  return harness.routeDebugElement!.componentInstance as LongFormPipelineShellComponent;
}

function steps(): string[] {
  return Array.from(root().querySelectorAll('cp-stepper li')).map((item) => text(item));
}

function plan(): StubPlanReview {
  return harness.routeDebugElement!.query(By.directive(StubPlanReview)).componentInstance as StubPlanReview;
}

function documentSurface(): StubDocumentReview {
  return harness.routeDebugElement!.query(By.directive(StubDocumentReview)).componentInstance as StubDocumentReview;
}

function documentAt(status: LongFormDraftStatus): LongFormAssembledDocument {
  return { status } as LongFormAssembledDocument;
}

/** A piece of work on the server, with a draft at the given status. */
function piece(status: LongFormDraftStatus, slug = SLUG): void {
  server.seed(slug, 'ctx-piece', { workingTitle: 'Soda bread, start to finish' });
  drafts.hold(slug, draftOf('d-piece', 'ctx-piece', status));
}

const PIECE = `${BASE}/context/ctx-piece/draft/d-piece`;

describe('longFormPipelineMatcher', () => {
  const match = (path: string) => {
    const segments = path === '' ? [] : path.split('/').map((part) => ({ path: part }) as never);
    const result = longFormPipelineMatcher(segments);

    return result === null ? null : Object.fromEntries(Object.entries(result.posParams ?? {}).map(([key, value]) => [key, value.path]));
  };

  it('reads every address of the journey as one route, and refuses any other shape', () => {
    expect(match('')).toEqual({});
    expect(match('context/c1')).toEqual({ contextId: 'c1' });
    expect(match('context/c1/about')).toEqual({ contextId: 'c1', step: 'about' });
    expect(match('context/c1/draft/d1')).toEqual({ contextId: 'c1', draftId: 'd1' });
    expect(match('context/c1/draft/d1/write')).toEqual({ contextId: 'c1', draftId: 'd1', step: 'write' });

    expect(match('about')).toBeNull();
    expect(match('context')).toBeNull();
    expect(match('context/c1/draft')).toBeNull();
    expect(match('context/c1/draft/d1/write/extra')).toBeNull();
  });
});

describe('LongFormPipelineShellComponent', () => {
  beforeEach(() => {
    localStorage.clear();
    server = new FakeCreativeContextService();
    drafts = new FakeLongFormDraftService();
    library = new FakeRecipeLibrary([{ slug: SLUG, id: 'r-soda', title: 'Soda bread', versionIds: ['v-soda-1'] }]);
    posts = null;
    allowance = signal<AiAllowanceState>({ kind: 'unknown' });
  });

  // ---- the shape of the page -----------------------------------------------------------------------------

  it('is a flow: one heading, the whole journey in the step list, and one card headed by the step', async () => {
    await create();

    expect(root().querySelectorAll('h1').length).toBe(1);
    expect(root().querySelector('h1')?.textContent).toContain('Long-form');
    expect(text(root().querySelector('a[cpBackLink]')!)).toContain('Workflows');

    // All four steps, in order, each with its state said in a word.
    expect(steps().length).toBe(4);
    expect(steps()[0]).toContain('What it is about');
    expect(steps()[1]).toContain('Outline');
    expect(steps()[2]).toContain('Write');
    expect(steps()[3]).toContain('Review and accept');
    expect(root().querySelector('cp-stepper [aria-current="step"]')?.textContent).toContain('What it is about');

    expect(root().querySelectorAll('cp-topic-card').length).toBe(1);
    expect(text(root().querySelector(`#${LONG_FORM_PIPELINE_HEADING_ID}`)!)).toBe('What it is about');
    expect(text(root().querySelector('[data-testid="legend"]')!)).toBe('Say what this piece is about. A recipe, a few words of your own, or both.');
    expect(text(root().querySelector('details.help')!)).toContain('An article and a newsletter issue are planned differently');
    expect(text(root().querySelector('cp-progress')!)).toContain('Step 1 of 4');

    // A live region that exists before it has anything to say.
    expect(root().querySelector('[role="status"][aria-live="polite"]')).not.toBeNull();
  });

  it('asks only what the piece is about, and shows the voice and the audience as read from the brand', async () => {
    await create({
      guide: { name: 'House voice', versionNumber: 3 },
      brand: {
        status: 'found',
        profile: { defaultAudience: 'busy weeknight cooks' } as BrandProfile,
      },
    });

    const brand = root().querySelector('[data-testid="brand-read"]')!;
    expect(text(brand)).toContain('Written in the voice of House voice, version 3.');
    expect(text(brand)).toContain('Written for busy weeknight cooks.');

    // Shown, never asked: nothing in that section can be typed into or chosen.
    expect(brand.querySelector('input, textarea, select, button')).toBeNull();

    // What is asked: the kind, the recipe, a title and a brief. Nothing about voice, audience or channels.
    const labels = Array.from(root().querySelectorAll('label')).map((label) => text(label));
    expect(labels.some((label) => label.includes('What it is called'))).toBeTrue();
    expect(labels.some((label) => label.includes('What this piece should say'))).toBeTrue();
    expect(labels.some((label) => /voice|audience|channel/i.test(label))).toBeFalse();
  });

  it('says so, and stops nothing, when the brand has no guide and no audience or cannot be read', async () => {
    await create();
    expect(text(root().querySelector('[data-testid="brand-read"]')!)).toContain('No style guide is active, so no brand voice is applied.');
    expect(text(root().querySelector('[data-testid="brand-read"]')!)).toContain('No audience is set in your brand profile.');

    await create({ guide: 'unavailable', brand: { status: 'unavailable' } });
    expect(text(root().querySelector('[data-testid="brand-read"]')!)).toContain('could not be read just now');
    expect(root().querySelector('cp-long-form-about-step')).not.toBeNull();
  });

  // ---- nothing happens for the creator -------------------------------------------------------------------

  it('creates nothing on arrival, and nothing is planned until the creator asks', async () => {
    await create();

    expect(server.creates).toEqual([]);
    expect(drafts.starts).toEqual([]);
    expect(localStorage.getItem(PLACE_KEY)).toBeNull();

    // Nothing to plan from yet, and the step says what it needs.
    expect(button('Plan the outline')?.getAttribute('aria-disabled')).toBe('true');
    expect(text(root().querySelector('[data-testid="needed"]')!)).toContain('Link a recipe, or say what the piece is called');

    // The one action that uses the allowance says that it does.
    const cost = button('Plan the outline')!.getAttribute('aria-describedby')!;
    expect(text(root().querySelector(`#${cost}`)!)).toBe('This uses some of your AI allowance. Nothing is written yet.');
  });

  it('plans a piece that has no recipe from the creator’s own words, and moves to the outline', async () => {
    await create();
    const before = shell();

    await type('cp-long-form-about-title', 'Soda bread, start to finish');
    await session().flush();
    await settle();

    // The first words put the work on the server, and the address follows without rebuilding the page.
    expect(server.creates.length).toBe(1);
    expect(router.url).toBe(`${BASE}/context/ctx-1/about`);
    expect(shell()).toBe(before);
    expect(button('Plan the outline')?.getAttribute('aria-disabled')).toBeNull();

    await click('Plan the outline');

    expect(drafts.starts.length).toBe(1);
    expect(drafts.starts[0]!.request).toEqual({ creativeContextId: 'ctx-1', kind: 'BlogArticle' });
    expect(drafts.starts[0]!.key).toMatch(/^[0-9a-f-]{36}$/);
    expect(server.added).toEqual([]);

    expect(router.url).toBe(`${BASE}/context/ctx-1/draft/d-new/outline`);
    expect(shell()).toBe(before);
    expect(plan().draftId()).toBe('d-new');
    expect(plan().outlineRequestId()).toBe('req-outline');

    // Focus is on the new step's name, and where the creator is has been said.
    expect(document.activeElement?.id).toBe(LONG_FORM_PIPELINE_HEADING_ID);
    expect(text(root().querySelector('[role="status"][aria-live="polite"]')!)).toBe('Outline. Step 2 of 4.');
    expect(text(root().querySelector('cp-progress')!)).toContain('Step 2 of 4');
  });

  it('sends the kind and the length the creator chose with the request, and to nothing else', async () => {
    await create();
    await type('cp-long-form-about-brief', 'A short note about bread.');

    root().querySelector<HTMLInputElement>('input[type="radio"][value="NewsletterIssue"]')!.click();
    await settle();
    await type('cp-long-form-about-length', '450');
    await click('Plan the outline');

    expect(drafts.starts[0]!.request).toEqual({ creativeContextId: 'ctx-1', kind: 'NewsletterIssue', targetWords: 450 });

    // For this piece only: no write went to the brand, which this page has no way to change.
    expect(server.patches.every((patch) => !('kind' in patch.patch))).toBeTrue();
  });

  it('hands a spent allowance to the notice, says a refusal in the server’s words, and retries a lost reply as the same request', async () => {
    await create();
    await type('cp-long-form-about-title', 'Soda bread');

    drafts.startOutcomes = [{ status: 'account_suspended', unit: 'Credits' } as LongFormStartOutcome];
    await click('Plan the outline');
    expect(router.url).toContain('/about');
    expect(root().querySelector('cp-ai-allowance-notice')).not.toBeNull();
    expect(root().querySelector('[data-testid="step-problem"]')).toBeNull();

    drafts.startOutcomes = [{ status: 'refused', code: 'x', message: 'That piece of work cannot be planned from.' }];
    await click('Plan the outline');
    expect(text(root().querySelector('[data-testid="step-problem"]')!)).toContain('That piece of work cannot be planned from.');

    drafts.startOutcomes = [{ status: 'unavailable' }];
    await click('Plan the outline');
    expect(text(root().querySelector('[data-testid="step-problem"]')!)).toContain('Nothing was used');

    await click('Plan the outline');
    expect(router.url).toContain('/draft/d-new/outline');

    const keys = drafts.starts.map((start) => start.key);
    expect(keys[3]).toBe(keys[2]!);
    expect(keys[2]).not.toBe(keys[1]!);
  });

  // ---- the journey's state is the server's ---------------------------------------------------------------

  const REACHED: readonly [LongFormDraftStatus, string, string][] = [
    ['Draft', 'outline', 'Outline'],
    ['Outlined', 'write', 'Write'],
    ['Writing', 'review', 'Review and accept'],
    ['Assembled', 'review', 'Review and accept'],
    ['Accepted', 'review', 'Review and accept'],
    ['NeedsReview', 'review', 'Review and accept'],
  ];

  for (const [status, step, label] of REACHED) {
    it(`opens a ${status} piece no further than ${label}, whatever step the address asks for`, async () => {
      await create({ url: `${PIECE}/review`, setup: () => piece(status) });

      expect(router.url).toBe(`${PIECE}/${step}`);
      expect(text(root().querySelector(`#${LONG_FORM_PIPELINE_HEADING_ID}`)!)).toBe(label);
    });
  }

  it('tells the truth in the step list and the progress figure: reaching the last step is not finishing it', async () => {
    await create({ url: `${PIECE}/review`, setup: () => piece('Writing') });

    expect(text(root().querySelector('cp-progress')!)).toContain('Step 4 of 4');
    expect(root().querySelector('cp-progress [role="progressbar"]')?.getAttribute('aria-valuenow')).toBe('75');
    expect(steps()[3]).not.toMatch(/done/i);

    documentSurface().accepted.emit(documentAt('Accepted'));
    await settle();

    expect(text(root().querySelector('cp-progress')!)).toContain('Accepted');
    expect(root().querySelector('cp-progress [role="progressbar"]')?.getAttribute('aria-valuenow')).toBe('100');
    expect(text(root().querySelector('[role="status"][aria-live="polite"]')!)).toBe('This piece is accepted. Nothing has been published.');
  });

  it('opens a piece of work named with no draft where its draft on the server stands', async () => {
    await create({ url: `${BASE}/context/ctx-piece`, setup: () => piece('Outlined') });

    expect(router.url).toBe(`${PIECE}/write`);
    expect(documentSurface().show()).toBe('write');
  });

  it('composes the outline surface, and moves to writing when the plan is approved without asking twice', async () => {
    await create({ url: `${PIECE}/outline`, setup: () => piece('Draft') });

    expect(plan().workspaceSlug()).toBe(SLUG);
    expect(button('Continue')?.disabled).toBeTrue();
    expect(text(root().querySelector('[data-testid="needed"]')!)).toBe('Approve the plan to start writing.');

    plan().approved.emit(draftOf('d-piece', 'ctx-piece', 'Outlined'));
    await settle();

    expect(router.url).toBe(`${PIECE}/write`);
    expect(leaveAsked).toBe(0);
    expect(document.activeElement?.id).toBe(LONG_FORM_PIPELINE_HEADING_ID);
  });

  it('gives writing and acceptance a step each from the one surface, and opens review once writing has begun', async () => {
    await create({ url: `${PIECE}/write`, setup: () => piece('Outlined') });

    expect(documentSurface().show()).toBe('write');
    expect(button('Continue')?.disabled).toBeTrue();
    expect(text(root().querySelector('[data-testid="needed"]')!)).toBe('Write at least one section to read the piece whole.');

    // The surface reads the piece as the creator writes; the shell learns from that, not from a second read.
    documentSurface().documentRead.emit(documentAt('Writing'));
    await settle();

    expect(button('Continue')?.disabled).toBeFalse();
    await click('Continue');

    expect(router.url).toBe(`${PIECE}/review`);
    expect(documentSurface().show()).toBe('accept');
    expect(button('Continue')).toBeNull();
    expect(text(root().querySelector('[data-testid="legend"]')!)).toBe(
      'Nothing is accepted until you say so, and accepting does not publish anything.',
    );
  });

  // ---- only the step reached is the device's -------------------------------------------------------------

  it('keeps on the device two ids and a step, and not a word the creator typed', async () => {
    await create();
    await type('cp-long-form-about-title', 'A secret working title');
    await type('cp-long-form-about-brief', 'What I really think about bread.');
    await session().flush();
    await settle();
    await click('Plan the outline');

    expect(decodeLongFormPipelinePlace(localStorage.getItem(PLACE_KEY))).toEqual({ contextId: 'ctx-1', draftId: 'd-new', step: 'outline' });

    const everything = Object.keys(localStorage).map((key) => `${key}=${localStorage.getItem(key)}`).join('\n');
    expect(everything).not.toContain('secret');
    expect(everything).not.toContain('really think');
    expect(Object.keys(localStorage)).toEqual([PLACE_KEY]);
  });

  it('offers to pick the piece up from the bare address, and the server overrules the step the device remembers', async () => {
    await create({
      setup: () => {
        piece('Draft');

        // The device remembers the last step; the piece on the server has only an unapproved plan.
        localStorage.setItem(PLACE_KEY, encodeLongFormPipelinePlace({ contextId: 'ctx-piece', draftId: 'd-piece', step: 'review' }));
      },
    });

    expect(text(root().querySelector('#cp-long-form-resume-heading')!)).toBe('Pick up where you left off');
    expect(text()).toContain('You were on Review and accept.');
    expect(root().querySelector('cp-stepper')).toBeNull();

    await click('Continue');

    expect(router.url).toBe(`${PIECE}/outline`);
  });

  it('starts a new piece from the resume offer without asking, and forgets the place', async () => {
    await create({
      setup: () => {
        piece('Draft');
        localStorage.setItem(PLACE_KEY, encodeLongFormPipelinePlace({ contextId: 'ctx-piece', draftId: 'd-piece', step: 'outline' }));
      },
    });

    await click('Start a new piece');

    expect(localStorage.getItem(PLACE_KEY)).toBeNull();
    expect(text(root().querySelector(`#${LONG_FORM_PIPELINE_HEADING_ID}`)!)).toBe('What it is about');

    // The piece itself is untouched: it is on the server, and nothing here deletes one.
    expect(drafts.drafts.has('d-piece')).toBeTrue();
  });

  // ---- started from the Workflows Hub (LF.6.3) -----------------------------------------------------------

  it('starts from a recipe with focus on the recipe search, and from a brief with focus on the brief', async () => {
    await create({ url: `${BASE}?from=recipe` });
    expect(document.activeElement?.id).toBe('cp-long-form-about-recipe-search');

    await create({ url: `${BASE}?from=brief` });
    expect(document.activeElement?.id).toBe('cp-long-form-about-brief');

    // Arriving created nothing, whichever way in was taken.
    expect(server.creates).toEqual([]);
    expect(drafts.starts).toEqual([]);
    expect(localStorage.getItem(PLACE_KEY)).toBeNull();
  });

  it('starts a new piece when asked to, rather than offering the last one in its place', async () => {
    await create({
      url: `${BASE}?from=brief`,
      setup: () => {
        piece('Draft');
        localStorage.setItem(PLACE_KEY, encodeLongFormPipelinePlace({ contextId: 'ctx-piece', draftId: 'd-piece', step: 'outline' }));
      },
    });

    expect(root().querySelector('#cp-long-form-resume-heading')).toBeNull();
    expect(text(root().querySelector(`#${LONG_FORM_PIPELINE_HEADING_ID}`)!)).toBe('What it is about');

    // The last piece is not thrown away by starting another: it is still what the bare address offers.
    expect(decodeLongFormPipelinePlace(localStorage.getItem(PLACE_KEY))?.draftId).toBe('d-piece');
  });

  it('takes a starting point it does not know as none at all', async () => {
    await create({
      url: `${BASE}?from=elsewhere`,
      setup: () => {
        piece('Draft');
        localStorage.setItem(PLACE_KEY, encodeLongFormPipelinePlace({ contextId: 'ctx-piece', draftId: 'd-piece', step: 'outline' }));
      },
    });

    expect(root().querySelector('#cp-long-form-resume-heading')).not.toBeNull();
  });

  it('ignores a place it cannot read rather than offering something broken', async () => {
    await create({ setup: () => localStorage.setItem(PLACE_KEY, '{"v":1,"contextId":"","step":"nowhere"}') });

    expect(root().querySelector('#cp-long-form-resume-heading')).toBeNull();
    expect(text(root().querySelector(`#${LONG_FORM_PIPELINE_HEADING_ID}`)!)).toBe('What it is about');
  });

  // ---- unsaved text is never dropped silently ------------------------------------------------------------

  it('asks the step on screen before Back, and stays when the creator keeps their unsaved text', async () => {
    await create({ url: `${PIECE}/write`, setup: () => piece('Writing') });

    leaveAnswer = false;
    await click('Back');
    expect(leaveAsked).toBe(1);
    expect(router.url).toBe(`${PIECE}/write`);

    leaveAnswer = true;
    await click('Back');
    expect(leaveAsked).toBe(2);
    expect(router.url).toBe(`${PIECE}/outline`);
  });

  it('asks through the route’s guard for any other way out, once', async () => {
    await create({ url: `${PIECE}/write`, setup: () => piece('Writing') });

    leaveAnswer = false;
    await router.navigateByUrl(`/${SLUG}/workflows`);
    await settle();
    expect(leaveAsked).toBe(1);
    expect(router.url).toBe(`${PIECE}/write`);

    // The browser's own Back between two steps is a way out of the step too.
    await router.navigateByUrl(`${PIECE}/outline`);
    await settle();
    expect(leaveAsked).toBe(2);
    expect(router.url).toBe(`${PIECE}/write`);

    leaveAnswer = true;
    await click('Save and exit');
    expect(leaveAsked).toBe(3);
    expect(router.url).toBe(`/${SLUG}/workflows`);
  });

  it('asks nothing on the first step, where every answer is saved as it is given', async () => {
    await create({ url: `${PIECE}/about`, setup: () => piece('Writing') });

    // The kind was chosen when the outline was asked for, and is shown rather than offered again.
    expect(text(root().querySelector('[data-testid="kind-settled"]')!)).toContain('A blog article.');
    expect(button('Plan the outline')).toBeNull();

    await click('Continue');
    expect(leaveAsked).toBe(0);
    expect(router.url).toBe(`${PIECE}/outline`);
  });

  // ---- teasers -------------------------------------------------------------------------------------------

  it('names the accepted posts of the same piece of work as its teasers, on the last step only', async () => {
    posts = {
      id: 'pkg-1',
      creativeContextId: 'ctx-piece',
      updatedAt: '2026-10-12T09:00:00Z',
      channels: [
        { channelKey: 'instagram', status: 'Accepted', isCurrent: true, updatedAt: 'x', latest: { body: 'Later wording.' }, accepted: { body: 'Soda bread, no kneading.' } },
        { channelKey: 'facebook', status: 'Proposed', isCurrent: null, updatedAt: 'x', latest: { body: 'A draft post.' }, accepted: null },
        { channelKey: 'pinterest', status: 'NeedsReview', isCurrent: false, updatedAt: 'x', latest: { body: 'Stale.' }, accepted: { body: 'An old pin.' } },
      ],
    } as unknown as ChannelPostPackage;

    await create({ url: `${PIECE}/review`, setup: () => piece('Writing') });

    const teasers = root().querySelector('[data-testid="teasers"]')!;
    expect(text(teasers)).toContain('Instagram');
    expect(text(teasers)).toContain('Soda bread, no kneading.');

    // Only what the creator accepted and is still current: not a proposal, not a later draft, not a stale post.
    expect(text(teasers)).not.toContain('A draft post.');
    expect(text(teasers)).not.toContain('Later wording.');
    expect(text(teasers)).not.toContain('An old pin.');
    expect(teasers.querySelector('input, textarea, button')).toBeNull();
    expect(root().querySelector('cp-long-form-teasers a')?.getAttribute('href')).toBe(
      `/${SLUG}/workflows/content-pipeline/context/ctx-piece/finish`,
    );

    await click('Back');
    expect(root().querySelector('cp-long-form-teasers')).toBeNull();
  });

  it('shows no teasers section when the piece of work has no accepted posts', async () => {
    await create({ url: `${PIECE}/review`, setup: () => piece('Writing') });

    expect(root().querySelector('[data-testid="teasers"]')).toBeNull();
    expect(text()).not.toContain('Posts that tease this piece');
  });

  // ---- who may be here, and what is theirs ---------------------------------------------------------------

  it('stops a Viewer at the door, and says nothing of a workspace the creator is not in', async () => {
    await create({ role: 'Viewer', url: `${PIECE}/write`, setup: () => piece('Writing') });
    expect(text()).toContain('view-only access');
    expect(root().querySelector('cp-stepper')).toBeNull();
    expect(root().querySelector('[data-testid="document-surface"]')).toBeNull();

    await create({ role: null });
    expect(text()).toContain('We couldn\'t find this workspace.');
    expect(drafts.starts).toEqual([]);
  });

  it('answers a draft from another workspace exactly as one that does not exist', async () => {
    // The same ids, in the workspace next door. This creator is a member of both.
    await create({ url: `${PIECE}/write`, setup: () => piece('Writing', OTHER_SLUG) });

    expect(text()).toContain('We couldn\'t find this piece.');
    expect(root().querySelector('[data-testid="document-surface"]')).toBeNull();
    expect(text()).not.toContain('Soda bread, start to finish');

    await create({ url: `${BASE}/context/ctx-none/draft/d-none/write` });
    expect(text()).toContain('We couldn\'t find this piece.');
  });

  it('re-reads the piece when the workspace changes under a page still open, even where the ids are the same', async () => {
    await create({ url: `${PIECE}/about`, setup: () => piece('Writing') });

    const title = () => root().querySelector<HTMLInputElement>('#cp-long-form-about-title');
    expect(title()?.value).toBe('Soda bread, start to finish');

    // The same page, reused by the router, now addressed to the workspace next door with the very same ids.
    const before = shell();
    await router.navigateByUrl(`/${OTHER_SLUG}/workflows/long-form/context/ctx-piece/draft/d-piece/about`);
    await settle();

    expect(shell()).toBe(before);
    expect(text()).toContain('We couldn\'t find this piece.');
    expect(title()).toBeNull();
    expect(text()).not.toContain('Soda bread, start to finish');

    // Nothing was written under the other workspace's key for a piece that is not its own.
    expect(localStorage.getItem('cp.longform.w2.m2')).toBeNull();
  });

  it('drops an outline answer that arrives after the creator has started another piece', async () => {
    await create();
    await type('cp-long-form-about-title', 'The first piece');
    await session().flush();
    await settle();

    // The request is held until the test lets it answer.
    let answer!: (outcome: LongFormStartOutcome) => void;
    drafts.start = (slug, request, key) => {
      drafts.starts.push({ slug, request, key });

      return new Promise<LongFormStartOutcome>((resolve) => (answer = resolve));
    };

    button('Plan the outline')!.click();
    await settle();
    expect(drafts.starts.length).toBe(1);

    await click('Start a new piece');
    expect(router.url).toBe(BASE);

    answer({ status: 'accepted', draft: draftOf('d-late', 'ctx-1', 'Draft'), operation: operation(), replayed: false });
    await settle();

    // The late draft is not adopted: the new piece is still at its first step, with no draft and no address.
    expect(router.url).toBe(BASE);
    expect(root().querySelector('[data-testid="plan-surface"]')).toBeNull();
    expect(button('Plan the outline')).not.toBeNull();
    expect(button('Plan the outline')?.textContent).toContain('Plan the outline');
  });

  it('starts the next piece from the defaults, not from what the last one was asked for', async () => {
    await create();
    await type('cp-long-form-about-title', 'A newsletter');
    root().querySelector<HTMLInputElement>('input[type="radio"][value="NewsletterIssue"]')!.click();
    await settle();
    await type('cp-long-form-about-length', '450');
    await session().flush();
    await settle();

    await click('Start a new piece');
    await type('cp-long-form-about-title', 'An article');
    await session().flush();
    await settle();
    await click('Plan the outline');

    expect(drafts.starts.at(-1)!.request.kind).toBe('BlogArticle');
    expect('targetWords' in drafts.starts.at(-1)!.request).toBeFalse();
  });

  it('does not open a draft under a piece of work that is not its own', async () => {
    await create({
      url: `${BASE}/context/ctx-other/draft/d-piece/write`,
      setup: () => {
        piece('Writing');
        server.seed(SLUG, 'ctx-other', { workingTitle: 'Another piece' });
      },
    });

    expect(text()).toContain('We couldn\'t find this piece.');
    expect(root().querySelector('[data-testid="document-surface"]')).toBeNull();
  });

  it('keeps each workspace’s place to itself on one device', async () => {
    await create({
      url: `/${OTHER_SLUG}/workflows/long-form`,
      setup: () => {
        piece('Draft');

        // Remembered for the first workspace. The second has its own key, and nothing under it.
        localStorage.setItem(PLACE_KEY, encodeLongFormPipelinePlace({ contextId: 'ctx-piece', draftId: 'd-piece', step: 'outline' }));
      },
    });

    expect(root().querySelector('#cp-long-form-resume-heading')).toBeNull();
    expect(text(root().querySelector(`#${LONG_FORM_PIPELINE_HEADING_ID}`)!)).toBe('What it is about');
    expect(text()).not.toContain('Soda bread, start to finish');
  });

  it('says a piece could not be loaded, with a way to try again, rather than showing an empty one', async () => {
    await create({
      url: `${PIECE}/write`,
      setup: () => {
        piece('Writing');
        drafts.unavailable = true;
      },
    });

    expect(text()).toContain('This piece couldn\'t be loaded right now.');

    drafts.unavailable = false;
    await click('Try again');

    expect(router.url).toBe(`${PIECE}/write`);
    expect(root().querySelector('[data-testid="document-surface"]')).not.toBeNull();
  });
});
