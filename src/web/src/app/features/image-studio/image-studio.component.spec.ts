import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { Observable, Subject, of } from 'rxjs';

import { ConfirmService } from '../../core/confirm.service';
import { WorkspaceRole } from '../../models/auth.models';
import {
  GeneratedImageOperationDetail,
  GeneratedImageOperationStatus,
  RequestGeneratedImagesRequest,
  StagedImage,
} from '../../models/generated-image.models';
import { ImageStudioDraft, emptyImageStudioDraft } from '../../models/image-studio.models';
import { AiAllowanceState, AiUsageService } from '../../services/ai-usage.service';
import { AiWatchOperationOutcome } from '../../services/ai-request';
import { BrandProfileService } from '../../services/brand-profile.service';
import { BrandLibraryOutcome, BrandSourceDocumentService } from '../../services/brand-source-document.service';
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
import { PhotographyConceptService } from '../../services/photography-concept.service';
import { ReferenceImageService } from '../../services/reference-image.service';
import { MyMembershipsState, WorkspaceMembershipService } from '../../services/workspace-membership.service';
import { ImageStudioComponent } from './image-studio.component';

const SLUG = 'cozy-fall';
const OTHER_SLUG = 'other-kitchen';
const PROMPT = 'A tight crop of the first slice, in soft morning light.';

/** The stub store keys the way the real one does: by workspace id and membership id, never by slug. */
const ownerKey = (owner: ImageStudioDraftOwner): string => `${owner.workspaceId}.${owner.membershipId}`;
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
let stored: Record<string, ImageStudioDraft | null>;
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
let watches: Subject<GeneratedImageWatchOutcome>[];

const draftStore = {
  read: (owner: ImageStudioDraftOwner): ImageStudioReadResult => {
    const discarded = discardNext;
    discardNext = false;

    return { draft: stored[ownerKey(owner)] ?? null, discarded };
  },
  write: (owner: ImageStudioDraftOwner, draft: ImageStudioDraft): boolean => {
    if (refuseWrites) return false;

    stored[ownerKey(owner)] = draft;
    writes.push({ key: ownerKey(owner), draft });

    return true;
  },
  clear: (owner: ImageStudioDraftOwner): void => {
    stored[ownerKey(owner)] = null;
    cleared.push(ownerKey(owner));
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
    discarded?: boolean;
    role?: WorkspaceRole | null;
    memberships?: 'ready' | 'loading' | 'error';
    url?: string;
  } = {},
): Promise<void> {
  stored = { ...(options.drafts ?? {}) };
  membershipSignal = membershipState(options.role === undefined ? 'Editor' : options.role, options.memberships ?? 'ready');
  discardNext = options.discarded === true;

  const neverAnswers = (): Observable<AiWatchOperationOutcome> => of<AiWatchOperationOutcome>();
  const ai = { request: () => Promise.resolve({ status: 'unavailable' as const }), watch: neverAnswers };

  TestBed.resetTestingModule();
  await TestBed.configureTestingModule({
    providers: [
      provideRouter([{ path: ':workspaceSlug/image-studio', component: ImageStudioComponent }]),
      { provide: ImageStudioDraftService, useValue: draftStore },
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
      { provide: PhotographyConceptService, useValue: ai },
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
        drafts: { 'w1.m1': draftWith({ images: { operationId: 'op-1', keepers: [] } }) },
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
        'Where it is going',
        'How many pictures to try',
        'What you already have in mind',
        'Scene',
        'Style',
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

  describe('getting around', () => {
    it('offers every part of the page as a destination that exists and can take focus', async () => {
      await create();

      const links = Array.from(root().querySelectorAll<HTMLAnchorElement>('cp-anchor-nav a'));
      expect(links.map((link) => link.textContent?.trim())).toEqual([
        'The brief',
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
      expect(text()).toContain('Kept on this device at');
    });

    it('brings a kept prompt back', async () => {
      await create({ drafts: { 'w1.m1': draftWith() } });

      expect(promptBox().value).toBe(PROMPT);
    });

    it('does not claim an edit was kept when the store refused it', async () => {
      await create();
      refuseWrites = true;

      await typePrompt(PROMPT);

      expect(promptBox().value).withContext('the edit stays on screen').toBe(PROMPT);
      expect(text()).toContain('not being kept on this device');
      expect(text()).not.toContain('Kept on this device at');
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

    it('shows what comes back, and keeps the run and the marks — and nothing about the pictures', async () => {
      await create({ drafts: { 'w1.m1': draftWith() } });
      await generate();

      expect(root().querySelectorAll('.sheet > li').length).toBe(2);
      expect(text()).toContain('Nothing is filed in your library from this page');

      root().querySelector<HTMLInputElement>('#cp-pipeline-keep-img-1')!.click();
      await settle();

      const last = writes[writes.length - 1].draft;
      expect(last.images).toEqual({ operationId: 'op-1', keepers: ['img-1'] });

      const kept = JSON.stringify(writes.map((write) => write.draft));
      for (const field of ['mediaType', 'sizeBytes', 'width', 'height', 'providerName', 'modelName', 'retentionExpiresAt']) {
        expect(kept).withContext(field).not.toContain(field);
      }
    });

    it('picks a kept run back up on return, without asking for a new one', async () => {
      await create({ drafts: { 'w1.m1': draftWith({ images: { operationId: 'op-1', keepers: [] } }) } });

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

  describe('two workspaces', () => {
    const cozy = draftWith({ images: { operationId: 'op-cozy', keepers: [] } });

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
