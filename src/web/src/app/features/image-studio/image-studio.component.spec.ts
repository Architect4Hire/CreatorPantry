import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { Observable, Subject, of } from 'rxjs';

import { ConfirmService } from '../../core/confirm.service';
import { WorkspaceRole } from '../../models/auth.models';
import { DamCreatedAsset } from '../../models/dam-asset.models';
import {
  GeneratedImageOperationDetail,
  GeneratedImageOperationStatus,
  RequestGeneratedImagesRequest,
  StagedImage,
} from '../../models/generated-image.models';
import { ImageStudioDraft, ImageStudioKeptWork, emptyImageStudioDraft } from '../../models/image-studio.models';
import { RequestPhotographyConceptsRequest } from '../../models/photography-concept.models';
import { AiAllowanceState, AiUsageService } from '../../services/ai-usage.service';
import { AiRequestOutcome, AiWatchOperationOutcome } from '../../services/ai-request';
import { ContentSeed, ContentSeedQuery } from '../../models/content-seed.models';
import { ContentSeedService } from '../../services/content-seed.service';
import { DishFacetService } from '../../services/dish-facet.service';
import { ReferenceService } from '../../services/reference.service';
import { BrandProfileService } from '../../services/brand-profile.service';
import { BrandLibraryOutcome, BrandSourceDocumentService } from '../../services/brand-source-document.service';
import { CreativeContextSession } from '../../services/creative-context-session';
import { FakeCreativeContextService } from '../../services/creative-context.fake';
import { CreativeContextService } from '../../services/creative-context.service';
import { CreativeContextFiling, CreativeContextFilingStore } from '../../services/device-draft-store';
import {
  GeneratedImageRequestOutcome,
  GeneratedImageService,
  GeneratedImageWatchOutcome,
} from '../../services/generated-image.service';
import {
  ImageStudioDraftOwner,
  ImageStudioDraftService,
  ImageStudioReadResult,
} from '../../services/image-studio-draft.service';
import { ImagePromptService } from '../../services/image-prompt.service';
import { DamAssetService } from '../../services/dam-asset.service';
import { FakeRecipeLibrary } from '../../services/recipe-library.fake';
import { RecipeService } from '../../services/recipe.service';
import { PhotographyConceptService } from '../../services/photography-concept.service';
import { ReferenceImageService } from '../../services/reference-image.service';
import { MyMembershipsState, WorkspaceMembershipService } from '../../services/workspace-membership.service';
import { SaveToLibraryDialogComponent } from '../../shared/save-to-library/save-to-library-dialog.component';
import { ImageStudioComponent } from './image-studio.component';

/** Every name sent to be read, so a spec can say how many readings typing bought. */
let nameAsks: string[];
let seedAsks: ContentSeedQuery[];

const facet = (key: string, displayName: string) => ({ key, displayName, pinned: false, fromRecipe: false });

/** The one idea the stub generator offers, about whatever name it was asked for. */
function idea(query: ContentSeedQuery): ContentSeed {
  return {
    token: 'abc-123',
    cuisine: facet(query.cuisine ?? 'thai', query.cuisine ?? 'Thai'),
    dishType: facet('salad', 'Salad'),
    method: { ...facet('grill', 'Grill'), requiresSafetyCaution: false },
    photographyStyle: facet('dark-and-moody', 'Dark and moody'),
    channel: null,
    day: { day: 'Wednesday', pinned: false, theme: null },
    occasion: facet('weeknight', 'Weeknight'),
    description: 'Develop a grilled Thai salad.',
    recipe: null,
    subject: query.subject ?? null,
  };
}

/** A finished reading of a name: Levantine, and nothing said about the rest. */
function nameRead(): AiRequestOutcome {
  const row = (fieldName: string, afterValue: string) => ({
    changeId: `c-${fieldName}`,
    changeKind: 'Set' as const,
    targetKind: 'DishFacetSuggestion' as const,
    targetId: 't1',
    fieldName,
    beforeValue: null,
    afterValue,
    proposedPosition: null,
    disposition: 'Pending' as const,
  });

  return {
    status: 'accepted',
    replayed: false,
    operation: {
      aiProposalRequestId: 'name-1',
      status: 'Proposed',
      taskType: 'DishFacetSuggestion',
      scope: 'NotApplicable',
      sourceVersionId: null,
      requestedAt: '2026-10-10T12:00:00Z',
      statusChangedAt: '2026-10-10T12:00:00Z',
      failureCategory: null,
      proposal: {
        proposalId: 'p1',
        outputSchemaVersion: '1',
        promptTemplateId: 'recipe.dish-facets',
        promptTemplateVersion: '1.0.0',
        promptTemplateBodyChecksum: 'abc',
        providerName: 'provider',
        modelName: 'model',
        createdAt: '2026-10-10T12:00:00Z',
        changes: [
          row('facet.Cuisine', 'levantine'),
          row('facet.Cuisine.confidence', 'Likely'),
          row('facet.Cuisine.rationale', 'Fattoush is a Levantine dish.'),
        ],
        warnings: [],
      },
    },
  };
}

const SLUG = 'cozy-fall';
const OTHER_SLUG = 'other-kitchen';
const PROMPT = 'A tight crop of the first slice, in soft morning light.';

/** The stub store keys the way the real one does: by workspace id and membership id, never by slug. */
const ownerKey = (owner: ImageStudioDraftOwner): string => `${owner.workspaceId}.${owner.membershipId}`;
/** The slug of the workspace an owner key belongs to, for putting its context on the fake server. */
const SLUG_OF: Readonly<Record<string, string>> = { w1: 'cozy-fall', w2: 'other-kitchen' };
const delay = (ms: number): Promise<void> => new Promise((resolve) => setTimeout(resolve, ms));

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

function picture(index: number): StagedImage {
  return {
    id: `img-${index}`,
    variantIndex: index,
    status: 'Staged',
    mediaType: 'image/png',
    width: 1024,
    height: 1024,
    sizeBytes: 482000,
    retentionExpiresAt: '2026-10-09T12:00:00Z',
    createdAt: '2026-10-08T12:00:00Z',
  };
}

function run(status: GeneratedImageOperationStatus, images: readonly StagedImage[]): GeneratedImageOperationDetail {
  return {
    id: 'op-1',
    status,
    variantCount: 2,
    stagedCount: images.length,
    providerName: 'Foundry',
    modelName: 'image-model',
    failureCategory: null,
    failureSummary: null,
    requestedAt: '2026-10-08T12:00:00Z',
    completedAt: null,
    images,
  };
}

function draftWith(overrides: Partial<ImageStudioDraft> = {}): ImageStudioDraft {
  const base = emptyImageStudioDraft(new Date('2026-10-08T12:00:00Z'));

  return { ...base, prompt: { ...base.prompt, finalPrompt: PROMPT, promptSource: 'creator' }, ...overrides };
}

let harness: RouterTestingHarness;
let server: FakeCreativeContextService;
let library: FakeRecipeLibrary;
/**
 * What this device holds for each owner — the work itself, whichever of the store's two places it is in.
 *
 * One record per owner on purpose: these specs are about the studio's behaviour, and "is this owner's work
 * kept, and under whose key" is the question they ask. Which place it is in is `unfiled` and `contextOf`.
 */
let stored: Record<string, ImageStudioDraft | null>;
/** Owners whose work is not on a context yet. */
let unfiled: Record<string, ImageStudioDraft | undefined>;
/** The context each owner's kept work is on, which is also the one this device last had open for them. */
let contextOf: Record<string, string | undefined>;
let filings: Record<string, CreativeContextFiling | undefined>;
let discardNext: boolean;
let writes: { readonly key: string; readonly draft: ImageStudioDraft }[];
let cleared: string[];
let refuseWrites: boolean;
let membershipSignal: ReturnType<typeof membershipState>;
let confirmAnswer: boolean;
let confirmCalls: string[];
let membershipLoads: number;
let requests: { readonly slug: string; readonly request: RequestGeneratedImagesRequest; readonly key: string }[];
let requestOutcome: GeneratedImageRequestOutcome;
let watchedSlugs: string[];
let conceptAsks: RequestPhotographyConceptsRequest[];
let watches: Subject<GeneratedImageWatchOutcome>[];

const draftStore = {
  read: (owner: ImageStudioDraftOwner): ImageStudioReadResult => {
    const discarded = discardNext;
    discardNext = false;

    return { draft: unfiled[ownerKey(owner)] ?? null, discarded };
  },
  write: (owner: ImageStudioDraftOwner, draft: ImageStudioDraft): boolean => {
    if (refuseWrites) return false;

    unfiled[ownerKey(owner)] = draft;
    stored[ownerKey(owner)] = draft;
    writes.push({ key: ownerKey(owner), draft });

    return true;
  },
  clear: (owner: ImageStudioDraftOwner): void => {
    const key = ownerKey(owner);
    delete unfiled[key];
    // Work that has moved onto a context is still held, under that context; only unfiled work goes with this.
    if (contextOf[key] === undefined) {
      stored[key] = null;
      cleared.push(key);
    }
  },
  readKept: (owner: ImageStudioDraftOwner, contextId: string): { kept: ImageStudioKeptWork | null; discarded: boolean } => {
    const key = ownerKey(owner);
    const draft = contextOf[key] === contextId ? (stored[key] ?? null) : null;

    return { kept: draft === null ? null : { draft, unsent: null }, discarded: false };
  },
  writeKept: (owner: ImageStudioDraftOwner, contextId: string, kept: ImageStudioKeptWork): boolean => {
    if (refuseWrites) return false;

    const key = ownerKey(owner);
    contextOf[key] = contextId;
    stored[key] = kept.draft;
    writes.push({ key, draft: kept.draft });

    return true;
  },
  clearKept: (owner: ImageStudioDraftOwner): void => {
    stored[ownerKey(owner)] = null;
    cleared.push(ownerKey(owner));
  },
  lastContextId: (owner: ImageStudioDraftOwner): string | null => contextOf[ownerKey(owner)] ?? null,
  rememberContext: (owner: ImageStudioDraftOwner, contextId: string): void => {
    contextOf[ownerKey(owner)] = contextId;
  },
  forgetContext: (owner: ImageStudioDraftOwner): void => {
    delete contextOf[ownerKey(owner)];
  },
  filing: (owner: ImageStudioDraftOwner): CreativeContextFilingStore => {
    const key = ownerKey(owner);

    return {
      read: () => filings[key] ?? null,
      write: (filing) => void (filings[key] = filing),
      clear: () => void delete filings[key],
    };
  },
  purgeAll: (): void => {
    stored = {};
  },
};

const images = {
  request: (
    slug: string,
    request: RequestGeneratedImagesRequest,
    key: string,
  ): Promise<GeneratedImageRequestOutcome> => {
    requests.push({ slug, request, key });

    return Promise.resolve(requestOutcome);
  },
  watch: (slug: string, operationId: string): Observable<GeneratedImageWatchOutcome> => {
    watchedSlugs.push(`${slug}|${operationId}`);
    const subject = new Subject<GeneratedImageWatchOutcome>();
    watches.push(subject);

    return subject.asObservable();
  },
  preview: () => of({ status: 'unavailable' as const }),
  reject: () => Promise.resolve({ status: 'declined' as const }),
  downloadUrl: (_slug: string, id: string) => `https://gateway.example/generated-images/${id}/content`,
};

async function settle(): Promise<void> {
  for (let i = 0; i < 4; i += 1) {
    await delay(0);
    harness.detectChanges();
  }
}

async function create(
  options: {
    drafts?: Record<string, ImageStudioDraft | null>;
    /** Whole drafts from before the studio's work lived on a context, sitting on this device alone. */
    unfiled?: Record<string, ImageStudioDraft>;
    offline?: boolean;
    setup?: (server: FakeCreativeContextService) => void;
    discarded?: boolean;
    role?: WorkspaceRole | null;
    memberships?: 'ready' | 'loading' | 'error';
    url?: string;
  } = {},
): Promise<void> {
  stored = { ...(options.drafts ?? {}) };
  unfiled = {};
  contextOf = {};
  filings = {};
  server = new FakeCreativeContextService();
  library = new FakeRecipeLibrary([
    { slug: SLUG, id: 'r-soda', title: 'Soda bread', versionIds: ['v-soda-1', 'v-soda-2'] },
    { slug: OTHER_SLUG, id: 'r-theirs', title: 'Their soda bread', versionIds: ['v-theirs-1'] },
  ]);

  // Work a spec starts with is work already on a creative context: its channel and picture are on the server,
  // the rest is on this device, and it is the context this device last had open for that owner.
  for (const [key, draft] of Object.entries(stored)) {
    if (draft === null) continue;

    contextOf[key] = `ctx-${key}`;
    server.seed(SLUG_OF[key.split('.')[0]], `ctx-${key}`, {
      channelKeys: draft.config.channelKey === null ? [] : [draft.config.channelKey],
      pictureBrief: draft.config.concept === '' ? null : draft.config.concept,
    });
  }
  for (const [key, draft] of Object.entries(options.unfiled ?? {})) {
    unfiled[key] = draft;
    stored[key] = draft;
  }
  options.setup?.(server);
  server.offline = options.offline === true;
  membershipSignal = membershipState(options.role === undefined ? 'Editor' : options.role, options.memberships ?? 'ready');
  discardNext = options.discarded === true;

  const neverAnswers = (): Observable<AiWatchOperationOutcome> => of<AiWatchOperationOutcome>();
  const ai = { request: () => Promise.resolve({ status: 'unavailable' as const }), watch: neverAnswers };
  conceptAsks = [];
  nameAsks = [];
  seedAsks = [];
  const entries = (...codes: string[]) =>
    Promise.resolve({
      status: 'found' as const,
      entries: codes.map((code) => ({ id: code, code, displayName: code })),
    });
  const concepts = {
    request: (_slug: string, request: RequestPhotographyConceptsRequest) => {
      conceptAsks.push(request);

      return Promise.resolve({ status: 'unavailable' as const });
    },
    watch: neverAnswers,
  };

  TestBed.resetTestingModule();
  await TestBed.configureTestingModule({
    providers: [
      provideRouter([
        { path: ':workspaceSlug/image-studio', component: ImageStudioComponent },
        { path: ':workspaceSlug/image-studio/context/:contextId', component: ImageStudioComponent },
      ]),
      { provide: ImageStudioDraftService, useValue: draftStore },
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
      {
        provide: WorkspaceMembershipService,
        useValue: {
          state: membershipSignal,
          ensureLoaded: () => Promise.resolve(),
          load: () => {
            membershipLoads += 1;

            return Promise.resolve();
          },
        },
      },
      {
        provide: BrandProfileService,
        useValue: { listContentChannels: () => Promise.resolve({ status: 'found', channels: [] }) },
      },
      {
        provide: ConfirmService,
        useValue: {
          confirm: (request: { title: string }) => {
            confirmCalls.push(request.title);

            return Promise.resolve(confirmAnswer);
          },
        },
      },
      { provide: PhotographyConceptService, useValue: concepts },
      {
        provide: ContentSeedService,
        useValue: {
          generate: (_slug: string, query: ContentSeedQuery) => {
            seedAsks.push(query);

            return of({ status: 'found' as const, seed: idea(query) });
          },
        },
      },
      {
        provide: DishFacetService,
        useValue: {
          request: (_slug: string, dishName: string) => {
            nameAsks.push(dishName);

            return Promise.resolve(nameRead());
          },
          watch: neverAnswers,
        },
      },
      {
        provide: ReferenceService,
        useValue: {
          listCuisines: () => entries('levantine', 'thai'),
          listCourses: () => entries('salad'),
          listTechniques: () =>
            Promise.resolve({
              status: 'found' as const,
              techniques: [{ id: 'grill', code: 'grill', displayName: 'grill', requiresSafetyCaution: false }],
            }),
          listPhotographyStyles: () => Promise.resolve({ status: 'unavailable' as const }),
          listOccasions: () => Promise.resolve({ status: 'unavailable' as const }),
        },
      },
      { provide: ImagePromptService, useValue: ai },
      { provide: ReferenceImageService, useValue: ai },
      {
        provide: AiUsageService,
        useValue: { allowance: signal<AiAllowanceState>({ kind: 'unknown' }), ensureLoaded: () => Promise.resolve() },
      },
      {
        provide: BrandSourceDocumentService,
        useValue: {
          searchLibrary: (): Observable<BrandLibraryOutcome> =>
            of({ status: 'ok', page: { items: [], nextCursor: null } }),
          upload: () => Promise.resolve({ status: 'failed' as const, reason: 'unavailable' as const }),
        },
      },
      { provide: GeneratedImageService, useValue: images },
    ],
  }).compileComponents();

  harness = await RouterTestingHarness.create();
  await harness.navigateByUrl(options.url ?? `/${SLUG}/image-studio`, ImageStudioComponent);
  await settle();
}

function root(): HTMLElement {
  return harness.routeNativeElement as HTMLElement;
}

/** The session of the studio on screen now — a navigation between address shapes builds a new one. */
function session(): CreativeContextSession {
  return harness.routeDebugElement!.injector.get(CreativeContextSession);
}

/** Send whatever the autosave is waiting to send, without waiting out its delay. */
async function saved(): Promise<void> {
  await session().flush();
  await settle();
}

async function typeConcept(value: string): Promise<void> {
  const concept = root().querySelector<HTMLTextAreaElement>('#cp-pipeline-concept')!;
  concept.value = value;
  concept.dispatchEvent(new Event('input'));
  await settle();
}

function subjectValue(): string {
  return root().querySelector<HTMLInputElement>('#cp-pipeline-subject')!.value;
}

async function typeSubject(value: string): Promise<void> {
  const field = root().querySelector<HTMLInputElement>('#cp-pipeline-subject')!;
  field.value = value;
  field.dispatchEvent(new Event('input'));
  await settle();
}

function conceptValue(): string {
  return root().querySelector<HTMLTextAreaElement>('#cp-pipeline-concept')!.value;
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

function promptBox(): HTMLTextAreaElement {
  return root().querySelector<HTMLTextAreaElement>('#cp-pipeline-final-prompt')!;
}

/** The "Save to library" button of one picture, by its place in the run. */
function saveButton(position: number, total = 2): HTMLButtonElement | null {
  return (
    Array.from(root().querySelectorAll<HTMLButtonElement>('button')).find(
      (each) => each.getAttribute('aria-label') === `Save picture ${position} of ${total} to your library`,
    ) ?? null
  );
}

/** The save form the run mounts, so what the studio does with its answers can be driven. */
function saveDialog(): SaveToLibraryDialogComponent {
  return harness.routeDebugElement!.query(By.directive(SaveToLibraryDialogComponent)).componentInstance;
}

function savedAsset(overrides: Partial<DamCreatedAsset> = {}): DamCreatedAsset {
  return {
    id: 'asset-1',
    title: 'The first slice',
    kind: 'AiGenerated',
    currentVersionNumber: 1,
    mediaType: 'image/png',
    width: 1024,
    height: 1024,
    sizeBytes: 482000,
    sourceGeneratedImageId: 'img-1',
    promptRecordId: null,
    recipeId: null,
    createdAt: '2026-10-09T12:00:00Z',
    ...overrides,
  };
}

async function typePrompt(value: string): Promise<void> {
  const box = promptBox();
  box.value = value;
  box.dispatchEvent(new Event('input'));
  await settle();
}

function accepted(): GeneratedImageRequestOutcome {
  return {
    status: 'accepted',
    operation: {
      id: 'op-1',
      status: 'Requested',
      variantCount: 2,
      stagedCount: 0,
      providerName: null,
      modelName: null,
      failureCategory: null,
      failureSummary: null,
      requestedAt: '2026-10-08T12:00:00Z',
      completedAt: null,
    },
  };
}

/** Ask for pictures and let a run in `status` with `count` of them arrive. */
async function generate(count = 2, status: GeneratedImageOperationStatus = 'Succeeded'): Promise<void> {
  requestOutcome = accepted();
  await click('Make the pictures');
  watches[watches.length - 1].next({
    status: 'found',
    operation: run(status, Array.from({ length: count }, (_value, index) => picture(index + 1))),
  });
  await settle();
}

describe('ImageStudioComponent', () => {
  beforeEach(() => {
    writes = [];
    cleared = [];
    refuseWrites = false;
    confirmAnswer = true;
    confirmCalls = [];
    membershipLoads = 0;
    requests = [];
    requestOutcome = { status: 'unavailable' };
    watchedSlugs = [];
    watches = [];
  });

  describe('before the studio opens', () => {
    it('says it is loading while the memberships are being read', async () => {
      await create({ memberships: 'loading' });

      expect(text()).toContain('Loading your studio');
      expect(root().querySelector('cp-form-section')).toBeNull();
    });

    it('says the workspaces could not be loaded, and offers to try again', async () => {
      await create({ memberships: 'error' });

      expect(root().querySelector('[role="alert"]')?.textContent).toContain("couldn't be loaded");

      await click('Try again');

      expect(membershipLoads).toBe(1);
    });

    it('answers an unknown workspace and one the creator is not in alike', async () => {
      await create({ url: '/never-created/image-studio' });
      const unknown = text();

      await create({ role: null });

      expect(text()).toBe(unknown);
      expect(text()).toContain("We couldn't find this workspace");
      expect(root().querySelector('cp-form-section')).toBeNull();
    });

    it('stops a Viewer at the door, and asks the server for nothing on their behalf', async () => {
      await create({
        role: 'Viewer',
        drafts: { 'w1.m1': draftWith({ images: { operationId: 'op-1' } }) },
      });

      expect(text()).toContain('view-only access');
      expect(root().querySelector('cp-form-section')).toBeNull();
      expect(watchedSlugs).toEqual([]);
    });
  });

  describe('the page', () => {
    it('lays the work out as named sections, in the order it happens', async () => {
      await create();

      const headings = Array.from(root().querySelectorAll('cp-form-section h2')).map((heading) =>
        heading.textContent?.trim(),
      );

      expect(headings).toEqual([
        'A recipe',
        // Directly under the picker, because the two answer the same question: a recipe from the library, or
        // the name of one that is not in it yet.
        'What it is',
        'Where it is going',
        'How many pictures to try',
        'What you already have in mind',
        'Scene',
        'Style',
        // The idea sits between the brief and the look, and is asked for rather than shown.
        'What you already know',
        'The look',
        'Extras',
        'The prompt',
        'Make the pictures',
      ]);
    });

    it('names every section to a screen reader', async () => {
      await create();

      for (const section of Array.from(root().querySelectorAll('cp-form-section section'))) {
        const labelledBy = section.getAttribute('aria-labelledby');
        expect(labelledBy).withContext('aria-labelledby').toBeTruthy();
        expect(root().querySelector('#' + labelledBy)?.textContent?.trim().length).toBeGreaterThan(0);
      }
    });

    it('asks for no day, because nothing here plans a week', async () => {
      await create();

      expect(text()).not.toContain('Which day is it for?');
    });

    it('says once what is needed, and marks no field optional or required', async () => {
      await create();

      expect(text()).toContain('Only the prompt is needed to make pictures');
      expect(root().querySelectorAll('[aria-required="true"]').length).toBe(0);
      expect(text()).not.toContain('(optional)');
    });

    it('has one polite live region, and announcements land in it', async () => {
      await create({ drafts: { 'w1.m1': draftWith() } });

      const regions = root().querySelectorAll('.studio > [role="status"][aria-live="polite"]');
      expect(regions.length).toBe(1);

      requestOutcome = accepted();
      await click('Make the pictures');

      expect(regions[0].textContent).toContain('Making 2 pictures.');
    });

    it('nests no control inside another', async () => {
      await create({ drafts: { 'w1.m1': draftWith() } });
      await generate();

      const controls = 'a[href], button, input, select, textarea, summary';
      for (const control of Array.from(root().querySelectorAll(controls))) {
        expect(control.querySelector(controls)).withContext(control.outerHTML.slice(0, 80)).toBeNull();
      }
    });
  });

  describe('an idea', () => {
    const select = (id: string): HTMLSelectElement => root().querySelector<HTMLSelectElement>(`#${id}`)!;

    it('does not read the name while it is being typed, only when asked', async () => {
      await create();

      await typeSubject('Fattoush');
      await typeSubject('Fattoush salad');

      expect(nameAsks).toEqual([]);

      await click('Fill these in from the name');
      await settle();

      expect(nameAsks).toEqual(['Fattoush salad']);
      expect(select('cp-pipeline-stated-cuisine').value).toBe('levantine');
      expect(text()).toContain('Suggested from the name');
    });

    it('reads the name before the first idea, so the idea is about the dish it names', async () => {
      await create();
      await typeSubject('Fattoush salad');

      await click('Suggest an idea');
      await settle();

      expect(nameAsks).toEqual(['Fattoush salad']);
      expect(seedAsks[seedAsks.length - 1].cuisine).toBe('levantine');
      expect(select('cp-pipeline-facet-cuisine').value).toBe('levantine');

      await click('Try another');
      await settle();

      expect(nameAsks.length).toBe(1);
    });

    it('offers each part of an idea its own picker, and no day to keep', async () => {
      await create();

      await click('Suggest an idea');

      expect(select('cp-pipeline-facet-cuisine').value).toBe('thai');
      expect(root().querySelector('#cp-pipeline-facet-day')).toBeNull();
      expect(root().querySelector('.facets')!.textContent).not.toContain('Wednesday');

      select('cp-pipeline-facet-cuisine').value = 'levantine';
      select('cp-pipeline-facet-cuisine').dispatchEvent(new Event('change'));
      await settle();

      expect(seedAsks[seedAsks.length - 1].cuisine).toBe('levantine');
      expect(seedAsks[seedAsks.length - 1].token).toBe('abc-123');
      expect(seedAsks[seedAsks.length - 1].day).toBeNull();
    });

    it('plans the looks from a picked idea when there is no description', async () => {
      await create();

      await click('Suggest an idea');
      await click('Pick this idea');
      await click('Plan some looks');

      expect(conceptAsks[0].creatorConcept).toBe('Develop a grilled Thai salad.');
    });

    it('plans the looks from the description until the creator chooses otherwise', async () => {
      await create();
      await typeConcept('A tight crop.');

      await click('Suggest an idea');
      await click('Pick this idea');
      await click('Plan some looks');

      expect(conceptAsks[0].creatorConcept).toBe('A tight crop.');
      expect(text()).toContain('You described a picture in the brief above');
    });

    it('counts a picked idea as something Start over would throw away', async () => {
      await create();

      await click('Suggest an idea');

      expect(button('Start over')!.disabled).toBeFalse();
    });
  });

  describe('getting around', () => {
    it('offers every part of the page as a destination that exists and can take focus', async () => {
      await create();

      const links = Array.from(root().querySelectorAll<HTMLAnchorElement>('cp-anchor-nav a'));
      expect(links.map((link) => link.textContent?.trim())).toEqual([
        'The brief',
        'An idea',
        'The look',
        'Extras',
        'The prompt',
        'The pictures',
      ]);

      for (const link of links) {
        const target = root().querySelector<HTMLElement>(link.getAttribute('href')!);
        expect(target).withContext(link.getAttribute('href')!).not.toBeNull();
        expect(target!.getAttribute('tabindex')).toBe('-1');
      }
    });

    it('moves focus to the part asked for, and marks it as the current one', async () => {
      await create();

      const link = Array.from(root().querySelectorAll<HTMLAnchorElement>('cp-anchor-nav a')).find(
        (each) => each.textContent?.trim() === 'The prompt',
      )!;
      const target = root().querySelector<HTMLElement>('#cp-studio-prompt')!;
      const scrolled = spyOn(target, 'scrollIntoView');

      link.click();
      await settle();

      // No `behavior`: the stylesheet decides, which is how reduced motion is honoured.
      expect(scrolled).toHaveBeenCalledWith({ block: 'start' });
      expect(document.activeElement).toBe(target);
      expect(link.getAttribute('aria-current')).toBe('true');
    });
  });

  describe('the prompt', () => {
    it('is there to write in before any look has been picked', async () => {
      await create();

      expect(promptBox()).not.toBeNull();
      expect(text()).toContain('Pick a look and a shot above');
    });

    it("keeps what the creator types as theirs, under this workspace's and this member's key", async () => {
      await create();

      await typePrompt(PROMPT);

      const last = writes[writes.length - 1];
      expect(last.key).toBe('w1.m1');
      expect(last.draft.prompt.finalPrompt).toBe(PROMPT);
      expect(last.draft.prompt.promptSource).toBe('creator');
      // The first answer is what gives the work its context; the prompt itself stays on this device.
      expect(server.creates.length).toBe(1);
      expect(server.creates[0].slug).toBe(SLUG);
      expect(text()).toContain('Your prompt, your picks and your pictures are kept on this device.');
    });

    it('brings a kept prompt back', async () => {
      await create({ drafts: { 'w1.m1': draftWith() } });

      expect(promptBox().value).toBe(PROMPT);
    });

    it('does not claim an edit was kept when the store refused it', async () => {
      // A lapsed session reaches neither this device's store nor the server.
      await create({ offline: true });
      refuseWrites = true;

      await typePrompt(PROMPT);

      expect(promptBox().value).withContext('the edit stays on screen').toBe(PROMPT);
      expect(text()).toContain('not being kept on this device');
      expect(text()).not.toContain('Saved at');
    });

    it('says so when something kept could not be read', async () => {
      await create({ discarded: true });

      expect(text()).toContain("couldn't be read");
    });
  });

  describe('making the pictures', () => {
    it('will not ask without a prompt, and says where to write one', async () => {
      await create();

      expect(button('Make the pictures')!.disabled).toBeTrue();
      expect(text()).toContain('There is no prompt yet. Write one above');
    });

    it('sends the prompt the creator typed, with no look picked, to the workspace in the route', async () => {
      await create();
      await typePrompt(`  ${PROMPT}  `);

      requestOutcome = accepted();
      await click('Make the pictures');

      expect(requests.length).toBe(1);
      expect(requests[0].slug).toBe(SLUG);
      expect(requests[0].request).toEqual({ promptText: PROMPT, avoidText: null, variantCount: 2 });
      expect(requests[0].key.length).toBeGreaterThan(0);
    });

    it('shows what comes back, and keeps the run — and nothing about the pictures', async () => {
      await create({ drafts: { 'w1.m1': draftWith() } });
      await generate();

      expect(root().querySelectorAll('.sheet > li').length).toBe(2);

      const last = writes[writes.length - 1].draft;
      expect(last.images).toEqual({ operationId: 'op-1' });

      const kept = JSON.stringify(writes.map((write) => write.draft));
      for (const field of ['mediaType', 'sizeBytes', 'width', 'height', 'providerName', 'modelName', 'retentionExpiresAt']) {
        expect(kept).withContext(field).not.toContain(field);
      }
    });

    it('offers saving as the one decision about a picture, and no keeper to mark as well (AF.4.2)', async () => {
      await create({ drafts: { 'w1.m1': draftWith() } });
      await generate();

      expect(text()).toContain('A picture you save is yours from then on');
      expect(text()).not.toContain('Nothing is filed in your library from this page');
      expect(root().querySelector('#cp-pipeline-keep-img-1')).toBeNull();
      expect(saveButton(1)).not.toBeNull();
      // An unsaved picture still goes, and the page says when.
      expect(text()).toContain('stop being available after');
      expect(text()).toContain('so save anything you want to keep.');
    });

    it('picks a kept run back up on return, without asking for a new one', async () => {
      await create({ drafts: { 'w1.m1': draftWith({ images: { operationId: 'op-1' } }) } });

      expect(watchedSlugs).toEqual([`${SLUG}|op-1`]);
      expect(requests).toEqual([]);
    });

    it('says a failed ask is safe to repeat, and repeats it under the same key', async () => {
      await create({ drafts: { 'w1.m1': draftWith() } });

      await click('Make the pictures');

      expect(root().querySelector('[role="alert"]')?.textContent).toContain('Trying again is safe');

      await click('Try again');

      expect(requests.length).toBe(2);
      expect(requests[1].key).toBe(requests[0].key);
    });

    it('tells a member who may not make pictures so, in words that fit this page', async () => {
      await create({ drafts: { 'w1.m1': draftWith() } });
      requestOutcome = { status: 'forbidden' };

      await click('Make the pictures');

      expect(text()).toContain('Ask an Owner or Editor to make them');
      expect(text()).not.toContain('run this step');
    });
  });

  describe('cancelling', () => {
    it('offers to stop checking and never to cancel, because nothing can', async () => {
      await create({ drafts: { 'w1.m1': draftWith() } });
      await generate(1, 'Running');

      expect(Array.from(root().querySelectorAll('button')).some((each) => /cancel/i.test(each.textContent ?? ''))).toBeFalse();

      await click('Stop checking');

      expect(text()).toContain('Nothing here cancels it');
      expect(button('Check again')).not.toBeNull();
    });

    it('lets go of a run that is still being watched when the page is left', async () => {
      await create({ drafts: { 'w1.m1': draftWith() } });
      await generate(1, 'Running');
      const watching = watches[watches.length - 1];
      const before = writes.length;

      TestBed.resetTestingModule();

      expect(watching.observed).toBeFalse();
      expect(writes.length).toBe(before);
    });
  });

  describe('starting over', () => {
    it('is not offered when there is nothing to throw away', async () => {
      await create();

      expect(button('Start over')!.disabled).toBeTrue();
    });

    it('asks first, and keeps everything when the answer is no', async () => {
      await create({ drafts: { 'w1.m1': draftWith() } });
      confirmAnswer = false;

      await click('Start over');

      expect(confirmCalls).toEqual(['Start this over?']);
      expect(cleared).toEqual([]);
      expect(promptBox().value).toBe(PROMPT);
    });

    it('throws the draft away, takes the pictures off the screen, and says so', async () => {
      await create({ drafts: { 'w1.m1': draftWith() } });
      await generate();
      const watching = watches[watches.length - 1];

      await click('Start over');

      expect(cleared).toEqual(['w1.m1']);
      expect(promptBox().value).toBe('');
      expect(root().querySelectorAll('.sheet > li').length).toBe(0);
      expect(root().querySelector('.studio > [role="status"]')?.textContent).toContain('Started over');

      // The run that was on screen is abandoned, not left to write itself back into the empty draft.
      const before = writes.length;
      watching.next({ status: 'found', operation: run('Succeeded', [picture(1), picture(2)]) });
      await settle();

      expect(root().querySelectorAll('.sheet > li').length).toBe(0);
      expect(writes.length).toBe(before);
    });
  });

  describe('on its creative context', () => {
    const STUDIO = `/${SLUG}/image-studio`;
    const url = (): string => TestBed.inject(Router).url;

    it('opens the work this device last had at its own address, where the context is read', async () => {
      await create({ drafts: { 'w1.m1': draftWith() } });

      expect(url()).toBe(`${STUDIO}/context/ctx-w1.m1`);
      expect(server.gets).toEqual([{ slug: SLUG, id: 'ctx-w1.m1' }]);
      expect(promptBox().value).toBe(PROMPT);
    });

    it('shows what the context holds with nothing kept on this device — a fresh browser profile', async () => {
      await create({
        url: `${STUDIO}/context/ctx-shared`,
        setup: (fake) =>
          fake.seed(SLUG, 'ctx-shared', { channelKeys: ['instagram'], pictureBrief: 'A tight crop of the first slice.' }),
      });

      expect(conceptValue()).toBe('A tight crop of the first slice.');
      expect(text()).toContain('Saved');
      expect(server.patches.length).withContext('reading is not an edit').toBe(0);
      expect(writes.length).withContext('opening leaves no record').toBe(0);
    });

    it('saves the picture to the context as it is typed, and leaves the day and theme a context came with alone', async () => {
      await create({
        url: `${STUDIO}/context/ctx-shared`,
        setup: (fake) => fake.seed(SLUG, 'ctx-shared', { day: 'Friday', weeklyThemeKey: 'fish-friday' }),
      });

      await typeConcept('A tight crop of the first slice.');
      await saved();

      expect(server.patches.length).toBe(1);
      expect(server.patches[0].patch).toEqual({ pictureBrief: 'A tight crop of the first slice.' });
      expect(server.stored(SLUG, 'ctx-shared')?.day).toBe('Friday');
      expect(server.stored(SLUG, 'ctx-shared')?.weeklyThemeKey).toBe('fish-friday');
    });

    it('reads an id that does not resolve as not found, never as an empty studio', async () => {
      await create({ url: `${STUDIO}/context/ctx-nowhere` });

      expect(text()).toContain("couldn't find this piece of work");
      expect(root().querySelector('#cp-pipeline-final-prompt')).toBeNull();
      expect(server.creates.length).toBe(0);
    });

    it("reads another workspace's context as not found", async () => {
      await create({
        url: `${STUDIO}/context/ctx-theirs`,
        setup: (fake) => fake.seed(OTHER_SLUG, 'ctx-theirs', { pictureBrief: 'Theirs.' }),
      });

      expect(text()).toContain("couldn't find this piece of work");
      expect(text()).not.toContain('Theirs.');
    });

    it('moves a draft from before contexts onto a new one once, and clears it from where it was', async () => {
      const old = draftWith({ config: { ...emptyImageStudioDraft().config, channelKey: 'instagram', concept: 'A tight crop.' } });
      await create({ unfiled: { 'w1.m1': old } });

      expect(server.creates.length).toBe(1);
      expect(server.creates[0].draft).toEqual({ channelKeys: ['instagram'], pictureBrief: 'A tight crop.' });
      expect(url()).toBe(`${STUDIO}/context/ctx-1`);
      expect(unfiled['w1.m1']).withContext('the old draft is cleared').toBeUndefined();
      expect(contextOf['w1.m1']).toBe('ctx-1');
      expect(promptBox().value).withContext('the prompt came along').toBe(PROMPT);
      expect(conceptValue()).toBe('A tight crop.');
    });

    it('leaves that draft exactly where it is when the move fails, and moves it on a retry — one context', async () => {
      const old = draftWith();
      await create({ unfiled: { 'w1.m1': old }, offline: true });

      expect(text()).toContain('still on this device');
      expect(root().querySelector('#cp-pipeline-final-prompt')).toBeNull();
      expect(unfiled['w1.m1']).toBe(old);

      server.offline = false;
      await click('Try again');

      expect(server.creates.length).toBe(1);
      expect(url()).toBe(`${STUDIO}/context/ctx-1`);
      expect(promptBox().value).toBe(PROMPT);
    });

    it('keeps an edit on screen when the server cannot be reached, says so, and sends it on a retry', async () => {
      await create({ drafts: { 'w1.m1': draftWith() } });
      server.offline = true;

      await typeConcept('Typed on a train.');
      await saved();

      expect(conceptValue()).toBe('Typed on a train.');
      expect(text()).toContain('not saved yet');

      server.offline = false;
      await click('Try again');

      expect(server.stored(SLUG, 'ctx-w1.m1')?.pictureBrief).toBe('Typed on a train.');
      expect(text()).not.toContain('not saved yet');
    });

    it('opens with the name the work was handed over under, so nothing is retyped', async () => {
      await create({
        url: `/${SLUG}/image-studio/context/ctx-shared`,
        setup: (fake) => fake.seed(SLUG, 'ctx-shared', { workingTitle: 'Fattoush Salad with Radishes' }),
      });

      // What the hand-off seeded. The studio opens knowing what the picture is of, which is what makes
      // "Plan some looks" available without the creator saying it again.
      expect(subjectValue()).toBe('Fattoush Salad with Radishes');
    });

    it('shows the newer name after a reload, because the context owns it and this screen only renders it', async () => {
      await create({
        url: `/${SLUG}/image-studio/context/ctx-shared`,
        setup: (fake) => fake.seed(SLUG, 'ctx-shared', { workingTitle: 'Soda bread' }),
      });
      server.changeElsewhere(SLUG, 'ctx-shared', { workingTitle: 'Barmbrack' });

      // A clash on this very field is what offers the reload: renamed here, renamed there.
      await typeSubject('Fattoush salad');
      await saved();
      await click('Load the latest');

      expect(confirmCalls).toEqual(['Load the latest?']);
      expect(subjectValue()).toBe('Barmbrack');
    });

    it('keeps the name typed here when the same field was changed somewhere else', async () => {
      await create({
        url: `/${SLUG}/image-studio/context/ctx-shared`,
        setup: (fake) => fake.seed(SLUG, 'ctx-shared', { workingTitle: 'Soda bread' }),
      });
      server.changeElsewhere(SLUG, 'ctx-shared', { workingTitle: 'Barmbrack' });

      await typeSubject('Fattoush salad');
      await saved();

      // Theirs until they say otherwise, exactly as the picture description is.
      expect(subjectValue()).toBe('Fattoush salad');
      expect(text()).toContain('changed somewhere else');
    });

    it('keeps the creator’s edit and offers a reload when the same field was changed somewhere else', async () => {
      await create({ drafts: { 'w1.m1': draftWith() } });
      server.changeElsewhere(SLUG, 'ctx-w1.m1', { pictureBrief: 'A wide shot of the table.' });

      await typeConcept('A tight crop, soft light.');
      await saved();

      expect(conceptValue()).toBe('A tight crop, soft light.');
      expect(text()).toContain('changed somewhere else');
      expect(server.stored(SLUG, 'ctx-w1.m1')?.pictureBrief).toBe('A wide shot of the table.');

      await click('Load the latest');

      expect(confirmCalls).toEqual(['Load the latest?']);
      expect(conceptValue()).toBe('A wide shot of the table.');
      expect(promptBox().value).withContext('what is only on this device is not touched').toBe(PROMPT);
    });
  });

  describe('saving a picture to the library (AF.4.2, FLU-005)', () => {
    /** Arrive on a context the server already holds, and make the pictures. */
    async function arrive(workingTitle: string | null = null): Promise<void> {
      await create({
        url: `/${SLUG}/image-studio/context/ctx-shared`,
        setup: (fake) => fake.seed(SLUG, 'ctx-shared', workingTitle === null ? {} : { workingTitle }),
      });
      await typePrompt(PROMPT);
      await generate();
    }

    /** …and press Save on the first picture. */
    async function openSave(): Promise<void> {
      await arrive();

      saveButton(1)!.click();
      await settle();
    }

    it('opens the save form for the picture pressed, with the work’s own title', async () => {
      await arrive('Autumn soda bread');

      saveButton(2)!.click();
      await settle();

      expect(saveDialog().open()).toBeTrue();
      expect(saveDialog().generatedImageId()).toBe('img-2');
      expect(saveDialog().workspaceSlug()).toBe(SLUG);
      expect(saveDialog().defaultTitle()).toBe('Autumn soda bread');
    });

    it('names the asset on the work’s creative context, pinned to the version that was saved', async () => {
      await openSave();

      saveDialog().saved.emit(savedAsset());
      await settle();

      expect(
        server.stored(SLUG, 'ctx-shared')?.references.map((each) => [each.kind, each.mediaAssetId, each.mediaAssetVersionNumber]),
      ).toEqual([['DamAsset', 'asset-1', 1]]);
      expect(text()).toContain('In your library as “The first slice”.');
      expect(root().querySelector(`a[href="/${SLUG}/dam/asset-1"]`)).not.toBeNull();
    });

    it('says the picture is saved even when the work could not be made to point at it', async () => {
      await openSave();

      server.refuseAddWith = 'limit';
      saveDialog().saved.emit(savedAsset());
      await settle();

      // Both halves of the truth: the picture is in the library, and this piece of work does not name it.
      expect(text()).toContain('is in your library, but this piece of work could not be made to point at it');
      expect(server.stored(SLUG, 'ctx-shared')?.references).toEqual([]);
      expect(text()).toContain('In your library as “The first slice”.');
    });

    it('offers no second save and no decline once a picture is in the library', async () => {
      await openSave();

      saveDialog().saved.emit(savedAsset());
      await settle();

      expect(saveButton(1)).toBeNull();
      expect(
        Array.from(root().querySelectorAll('button')).some(
          (each) => each.getAttribute('aria-label') === 'Decline picture 1 of 2',
        ),
      ).toBeFalse();
    });

    it('writes no keeper to the draft: on this page saving is the whole decision', async () => {
      await openSave();

      saveDialog().saved.emit(savedAsset());
      await settle();

      // Nothing but the run id is kept about the pictures: on this page saving is the whole decision.
      expect(writes.every((write) => Object.keys(write.draft.images).length === 1)).toBeTrue();
    });

    it('offers no save at all to a workspace this creator may only read', async () => {
      await create({ role: 'Viewer' });

      expect(saveButton(1)).toBeNull();
      expect(text()).toContain('view-only access');
    });
  });

  describe('a linked recipe', () => {
    async function linkSodaBread(): Promise<void> {
      const box = root().querySelector<HTMLInputElement>('#cp-studio-recipe-search')!;
      box.focus();
      box.value = 'soda';
      box.dispatchEvent(new Event('input'));
      await delay(300);
      await settle();

      const option = root().querySelector<HTMLElement>('#cp-studio-recipe [role="option"]')!;
      option.dispatchEvent(new MouseEvent('mousedown', { bubbles: true }));
      option.click();
      await settle();
    }

    it('is chosen in the brief and stored on the context, pinned to its current version', async () => {
      await create({ url: `/${SLUG}/image-studio/context/ctx-shared`, setup: (fake) => fake.seed(SLUG, 'ctx-shared') });

      await linkSodaBread();

      expect(
        server.stored(SLUG, 'ctx-shared')?.references.map((each) => [each.kind, each.recipeId, each.recipeVersionId]),
      ).toEqual([['Recipe', 'r-soda', 'v-soda-2']]);
      expect(text()).toContain('Version 2, as it was when you linked it.');
    });

    it('is named, with its pinned version, on the photography-concept request', async () => {
      await create({ url: `/${SLUG}/image-studio/context/ctx-shared`, setup: (fake) => fake.seed(SLUG, 'ctx-shared') });
      await linkSodaBread();
      await typeConcept('A tight crop.');

      await click('Plan some looks');

      expect(conceptAsks.length).toBe(1);
      expect(conceptAsks[0].recipeId).toBe('r-soda');
      expect(conceptAsks[0].recipeVersionId).toBe('v-soda-2');
    });

    it('is not named at all when nothing is linked', async () => {
      await create({ url: `/${SLUG}/image-studio/context/ctx-shared`, setup: (fake) => fake.seed(SLUG, 'ctx-shared') });
      await typeConcept('A tight crop.');

      await click('Plan some looks');

      expect(conceptAsks[0].recipeId).toBeNull();
      expect(conceptAsks[0].recipeVersionId).toBeNull();
    });

    it("never searches another workspace's recipes, and never offers one", async () => {
      await create({ url: `/${SLUG}/image-studio/context/ctx-shared`, setup: (fake) => fake.seed(SLUG, 'ctx-shared') });
      await linkSodaBread();

      expect(library.searches.every((search) => search.slug === SLUG)).toBeTrue();
      expect(text()).not.toContain('Their soda bread');
    });

    it('gives Start over something to throw away, and is unlinked from this screen by it', async () => {
      await create({ url: `/${SLUG}/image-studio/context/ctx-shared`, setup: (fake) => fake.seed(SLUG, 'ctx-shared') });
      await linkSodaBread();

      expect(button('Start over')!.disabled).toBeFalse();
      await click('Start over');

      expect(text()).not.toContain('Version 2, as it was when you linked it.');
      expect(root().querySelector('#cp-studio-recipe-search')).not.toBeNull();
    });
  });

  describe('two workspaces', () => {
    const cozy = draftWith({ images: { operationId: 'op-cozy' } });

    it("never shows one workspace's prompt in the other", async () => {
      await create({ drafts: { 'w1.m1': cozy } });

      await harness.navigateByUrl(`/${OTHER_SLUG}/image-studio`);
      await settle();

      expect(promptBox().value).toBe('');
      expect(text()).not.toContain(PROMPT);
    });

    it("never asks one workspace for the other's run", async () => {
      await create({ drafts: { 'w1.m1': cozy } });

      await harness.navigateByUrl(`/${OTHER_SLUG}/image-studio`);
      await settle();

      expect(watchedSlugs).toEqual([`${SLUG}|op-cozy`]);
    });

    it("never lets one workspace's late answer land on what the other is looking at", async () => {
      await create({ drafts: { 'w1.m1': cozy } });
      const late = watches[0];

      await harness.navigateByUrl(`/${OTHER_SLUG}/image-studio`);
      await settle();

      late.next({ status: 'found', operation: run('Succeeded', [picture(1), picture(2)]) });
      await settle();

      expect(late.observed).toBeFalse();
      expect(root().querySelectorAll('.sheet > li').length).toBe(0);
    });

    it("keeps what is typed in the second workspace under its own key, and leaves the first's alone", async () => {
      await create({ drafts: { 'w1.m1': cozy } });

      await harness.navigateByUrl(`/${OTHER_SLUG}/image-studio`);
      await settle();
      await typePrompt('A wide shot of the whole table.');

      expect(writes.every((write) => write.key === 'w2.m2')).toBeTrue();
      expect(stored['w1.m1']).toBe(cozy);
      expect(stored['w2.m2']?.prompt.finalPrompt).toBe('A wide shot of the whole table.');
    });

    it("sends the second workspace's ask to the second workspace", async () => {
      await create({ drafts: { 'w1.m1': cozy, 'w2.m2': draftWith() } });

      await harness.navigateByUrl(`/${OTHER_SLUG}/image-studio`);
      await settle();
      requestOutcome = accepted();
      await click('Make the pictures');

      expect(requests.map((request) => request.slug)).toEqual([OTHER_SLUG]);
    });

    it('shows nothing kept, and writes nothing, on the way to a workspace the creator is not in', async () => {
      await create({ drafts: { 'w1.m1': cozy } });
      const before = writes.length;

      await harness.navigateByUrl('/never-created/image-studio');
      await settle();

      expect(text()).toContain("We couldn't find this workspace");
      expect(text()).not.toContain(PROMPT);
      expect(writes.length).toBe(before);
    });

    it("never hands one member their colleague's draft in the same workspace", async () => {
      await create({ drafts: { 'w1.m1': cozy } });

      // The same browser and the same workspace, read by a different membership.
      membershipSignal.set({
        status: 'ready',
        memberships: [
          {
            workspaceId: 'w1',
            workspaceSlug: SLUG,
            workspaceName: 'Cozy Fall',
            membershipId: 'm-colleague',
            role: 'Editor',
            status: 'Active',
          },
        ],
      });
      await settle();

      expect(promptBox().value).toBe('');
      expect(text()).not.toContain(PROMPT);

      await typePrompt('Theirs.');

      expect(writes[writes.length - 1].key).toBe('w1.m-colleague');
      expect(stored['w1.m1']).toBe(cozy);
    });

    it("starts over in the workspace on screen only", async () => {
      await create({ drafts: { 'w1.m1': cozy, 'w2.m2': draftWith() } });

      await harness.navigateByUrl(`/${OTHER_SLUG}/image-studio`);
      await settle();
      await click('Start over');

      expect(cleared).toEqual(['w2.m2']);
      expect(stored['w1.m1']).toBe(cozy);
    });
  });
});
