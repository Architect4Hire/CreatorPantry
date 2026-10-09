import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { EMPTY, Observable, Subject } from 'rxjs';

import { ConfirmService } from '../../core/confirm.service';
import {
  CONTENT_PIPELINE_LIMITS,
  ContentPipelineDraft,
  ContentPipelineImagesState,
  emptyContentPipelineDraft,
} from '../../models/content-pipeline.models';
import {
  GeneratedImageOperationDetail,
  GeneratedImageOperationStatus,
  RequestGeneratedImagesRequest,
  StagedImage,
} from '../../models/generated-image.models';
import { CreativeContextSession } from '../../services/creative-context-session';
import { FakeCreativeContextService } from '../../services/creative-context.fake';
import { CreativeContextService } from '../../services/creative-context.service';
import {
  GeneratedImageRequestOutcome,
  GeneratedImageService,
  GeneratedImageWatchOutcome,
  StagedImageRejectOutcome,
} from '../../services/generated-image.service';
import { ContentPipelineImagesStepComponent } from './content-pipeline-images-step.component';

const PROMPT = 'A tight crop in soft morning light.';
const SLUG = 'cozy-fall';
const CTX = 'ctx-1';

let server: FakeCreativeContextService;

function picture(index: number, overrides: Partial<StagedImage> = {}): StagedImage {
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
    ...overrides,
  };
}

function run(
  status: GeneratedImageOperationStatus,
  images: readonly StagedImage[],
  overrides: Partial<GeneratedImageOperationDetail> = {},
): GeneratedImageOperationDetail {
  return {
    id: 'op-1',
    status,
    variantCount: 2,
    stagedCount: images.length,
    providerName: null,
    modelName: null,
    failureCategory: null,
    failureSummary: null,
    requestedAt: '2026-10-08T12:00:00Z',
    completedAt: null,
    images,
    ...overrides,
  };
}

function draftWith(
  prompt: string = PROMPT,
  images: Partial<ContentPipelineImagesState> = {},
  variantCount = 2,
): ContentPipelineDraft {
  const base = emptyContentPipelineDraft(new Date('2026-10-08T12:00:00Z'));

  return {
    ...base,
    config: { ...base.config, variantCount },
    prompt: {
      ...base.prompt,
      finalPrompt: prompt,
      promptSource: 'composed',
      generated: { text: prompt, avoid: ['clutter', 'harsh light'] },
    },
    images: { operationId: null, ...images },
    furthestStep: 'images',
  };
}

let initialDraft: ContentPipelineDraft = draftWith();

@Component({
  imports: [ContentPipelineImagesStepComponent],
  providers: [CreativeContextSession],
  template: `<cp-content-pipeline-images-step
    [workspaceSlug]="workspaceSlug()"
    [draft]="draft()"
    [session]="session"
    (changed)="apply($event)"
    (announced)="announcements.push($event)"
  />`,
})
class HostComponent {
  readonly workspaceSlug = signal('cozy-fall');
  readonly draft = signal<ContentPipelineDraft>(initialDraft);
  readonly announcements: string[] = [];

  constructor(readonly session: CreativeContextSession) {}

  apply(next: ContentPipelineDraft): void {
    this.draft.set(next);
  }
}

let fixture: ComponentFixture<HostComponent>;
let host: HostComponent;
let el: HTMLElement;

let requests: { readonly slug: string; readonly request: RequestGeneratedImagesRequest; readonly key: string }[];
let watchedSlugs: string[];
let requestOutcome: GeneratedImageRequestOutcome;
let watches: Subject<GeneratedImageWatchOutcome>[];
let rejected: string[];
let rejectOutcomes: Record<string, StagedImageRejectOutcome>;
let confirmAnswer: boolean;
let confirmCalls: { readonly title: string }[];

async function settle(): Promise<void> {
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
}

const service = {
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
  preview: () => EMPTY,
  reject: (slug: string, id: string): Promise<StagedImageRejectOutcome> => {
    rejected.push(`${slug}|${id}`);

    return Promise.resolve(rejectOutcomes[id] ?? { status: 'declined' });
  },
  downloadUrl: (_slug: string, id: string) =>
    `https://gateway.example/api/v1/workspaces/cozy-fall/generated-images/${id}/content`,
};

/** Mount the step on a context the server already holds, which is what the shell always hands it. */
async function mount(draft: ContentPipelineDraft = draftWith(), keepers: readonly string[] = []): Promise<void> {
  initialDraft = draft;
  server = new FakeCreativeContextService();
  server.seed(SLUG, CTX, {
    references: keepers.map((generatedImageId, index) =>
      server.reference({ kind: 'GeneratedImage', purpose: 'Keeper', generatedImageId }, index),
    ),
  });

  TestBed.resetTestingModule();
  await TestBed.configureTestingModule({
    providers: [
      { provide: GeneratedImageService, useValue: service },
      { provide: CreativeContextService, useValue: server },
      {
        provide: ConfirmService,
        useValue: {
          confirm: (request: { title: string }) => {
            confirmCalls.push({ title: request.title });

            return Promise.resolve(confirmAnswer);
          },
        },
      },
    ],
  }).compileComponents();

  fixture = TestBed.createComponent(HostComponent);
  host = fixture.componentInstance;
  el = fixture.nativeElement;
  await host.session.load(SLUG, CTX);
  await settle();
}

/** The pictures the work names as keepers, as the "server" holds them. */
function keepers(): readonly string[] {
  return (server.stored(SLUG, CTX)?.references ?? [])
    .filter((reference) => reference.purpose === 'Keeper')
    .map((reference) => reference.generatedImageId!)
    .filter((id) => id !== null);
}

function text(): string {
  return el.textContent ?? '';
}

function buttonWith(label: string): HTMLButtonElement | null {
  return (
    (Array.from(el.querySelectorAll('button')).find(
      (button) => (button.textContent ?? '').trim() === label,
    ) as HTMLButtonElement | undefined) ?? null
  );
}

async function click(label: string): Promise<void> {
  buttonWith(label)!.click();
  await settle();
}

/** The keep box of one tile, by the id the grid gives it. */
function keepBox(id: string): HTMLInputElement {
  return el.querySelector<HTMLInputElement>(`#cp-pipeline-keep-${id}`)!;
}

function labelled(selector: string, name: string): HTMLElement | null {
  return (
    Array.from(el.querySelectorAll<HTMLElement>(selector)).find(
      (node) => node.getAttribute('aria-label') === name,
    ) ?? null
  );
}

/** Ask for pictures and let a finished run with `count` of them arrive. */
async function generate(count = 2, status: GeneratedImageOperationStatus = 'Succeeded'): Promise<void> {
  requestOutcome = {
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

  await click('Make the pictures');
  watches[watches.length - 1].next({
    status: 'found',
    operation: run(
      status,
      Array.from({ length: count }, (_value, index) => picture(index + 1)),
    ),
  });
  await settle();
}

describe('ContentPipelineImagesStepComponent', () => {
  beforeEach(() => {
    requests = [];
    watchedSlugs = [];
    watches = [];
    rejected = [];
    rejectOutcomes = {};
    confirmAnswer = true;
    confirmCalls = [];
    requestOutcome = { status: 'unavailable' };
  });

  describe('before anything has been asked for', () => {
    it('shows the prompt that will be sent, and what it will avoid, before anything is pressed', async () => {
      await mount();

      expect(el.querySelector('.recap blockquote')?.textContent).toBe(PROMPT);
      expect(text()).toContain('clutter; harsh light');
      expect(text()).toContain('Asking for 2 pictures');
    });

    it('refuses to ask without a prompt, and says where to write one', async () => {
      await mount(draftWith(''));

      expect(text()).toContain('There is no prompt yet');
      expect(buttonWith('Make the pictures')?.disabled).toBeTrue();
    });

    it('refuses a prompt longer than the route takes, rather than earning a refusal', async () => {
      await mount(draftWith('a'.repeat(CONTENT_PIPELINE_LIMITS.promptMaxLength + 1)));

      expect(text()).toContain('longer than we can send');
      expect(text()).toContain(`of ${CONTENT_PIPELINE_LIMITS.promptMaxLength} characters`);
      expect(buttonWith('Make the pictures')?.disabled).toBeTrue();
      expect(requests).toEqual([]);
    });

    it('offers nothing to choose from until there is something in it', async () => {
      await mount();

      expect(text()).not.toContain('Choose the keepers');
    });
  });

  describe('asking for pictures', () => {
    it('sends the prompt, the avoid list and the count the creator set', async () => {
      await mount();
      await generate();

      expect(requests.length).toBe(1);
      expect(requests[0].request).toEqual({
        promptText: PROMPT,
        avoidText: 'clutter; harsh light',
        variantCount: 2,
      });
      expect(requests[0].key.length).toBeGreaterThan(0);
    });

    it('keeps the run on the draft, so a refresh finds the pictures again', async () => {
      await mount();
      await generate();

      expect(host.draft().images.operationId).toBe('op-1');
    });

    it('fills the sheet in as pictures land, and says how far through it is', async () => {
      await mount();
      requestOutcome = {
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
      await click('Make the pictures');

      watches[0].next({ status: 'found', operation: run('Running', [picture(1)]) });
      await settle();

      expect(text()).toContain('Making your pictures');
      expect(el.querySelector('[role="progressbar"]')?.getAttribute('aria-valuetext')).toBe('1 of 2 ready');
      expect(el.querySelectorAll('.sheet > li').length).toBe(1);
    });

    it('says the reading is stale rather than passing it off as live', async () => {
      await mount();
      await generate(1, 'Running');

      watches[watches.length - 1].next({ status: 'unavailable' });
      await settle();

      expect(text()).toContain('last update we could get');
    });

    it('says plainly when fewer pictures arrived than were asked for', async () => {
      await mount();
      await generate(1, 'PartiallySucceeded');

      expect(text()).toContain('One picture of the 2 asked for arrived');
      expect(text()).toContain('Choose the keepers');
    });

    it('explains a run that produced nothing, in the server’s own words', async () => {
      await mount();
      requestOutcome = {
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
      await click('Make the pictures');
      watches[0].next({
        status: 'found',
        operation: run('Failed', [], {
          failureCategory: 'provider-refused',
          failureSummary: 'This request was turned down.',
        }),
      });
      await settle();

      expect(text()).toContain('This request was turned down.');
      expect(text()).not.toContain('Choose the keepers');
    });

    it('names what made the pictures, which a creator deciding to publish is entitled to know', async () => {
      await mount();
      requestOutcome = {
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
      await click('Make the pictures');
      watches[0].next({
        status: 'found',
        operation: run('Succeeded', [picture(1)], { providerName: 'Foundry', modelName: 'image-1' }),
      });
      await settle();

      expect(text()).toContain('Made by Foundry, image-1.');
    });

    it('reports each refusal in its own terms', async () => {
      await mount();

      requestOutcome = { status: 'forbidden' };
      await click('Make the pictures');
      expect(text()).toContain('do not have permission');

      requestOutcome = { status: 'rate_limited' };
      await click('Try again');
      expect(text()).toContain('came through too quickly');

      requestOutcome = {
        status: 'refused',
        message: 'Ask for between 1 and 4 images.',
        fieldErrors: {},
      };
      await click('Try again');
      expect(text()).toContain('Ask for between 1 and 4 images.');
    });

    it('replays one key after an answer that never arrived, so a retry cannot buy a second set', async () => {
      await mount();

      requestOutcome = { status: 'unavailable' };
      await click('Make the pictures');
      await click('Try again');

      expect(requests.length).toBe(2);
      expect(requests[1].key).withContext('the same question').toBe(requests[0].key);
    });

    it('mints a new key for a deliberately different question', async () => {
      await mount();
      await generate();

      await click('Make new pictures');

      expect(requests.length).toBe(2);
      expect(requests[1].key).not.toBe(requests[0].key);
    });

    it('asks before replacing pictures that are on screen, and leaves them alone on a no', async () => {
      await mount(draftWith(), ['img-1']);
      await generate();

      confirmAnswer = false;
      await click('Make new pictures');

      expect(confirmCalls.length).toBe(1);
      expect(requests.length).withContext('nothing asked for').toBe(1);
      expect(keepers()).toEqual(['img-1']);
    });

    it('offers the previous run back rather than leaving an error with nothing behind it', async () => {
      await mount(draftWith(PROMPT, { operationId: 'op-old' }));

      requestOutcome = { status: 'unavailable' };
      // Past the resume that mounting starts, which is already watching op-old.
      watches[0].next({ status: 'found', operation: run('Succeeded', [picture(1)]) });
      await settle();
      await click('Make new pictures');

      expect(text()).toContain('Show the pictures from your last run');

      await click('Show the pictures from your last run');

      // The first watch was the resume on arrival; the failed ask started none; this is the second.
      expect(watches.length).withContext('watching it again').toBe(2);
      watches[1].next({ status: 'found', operation: run('Succeeded', [picture(1)]) });
      await settle();

      expect(el.querySelectorAll('.sheet > li').length).toBe(1);
    });

    it('picks a kept run back up on arrival, without being asked', async () => {
      await mount(draftWith(PROMPT, { operationId: 'op-old' }));

      expect(watches.length).toBe(1);

      watches[0].next({ status: 'found', operation: run('Succeeded', [picture(1), picture(2)]) });
      await settle();

      expect(el.querySelectorAll('.sheet > li').length).toBe(2);
      expect(requests).withContext('resuming asks for nothing new').toEqual([]);
    });

    it('stops checking without claiming to cancel, and offers to pick it up again', async () => {
      await mount();
      await generate(1, 'Running');

      await click('Stop checking');

      expect(text()).toContain('Nothing here cancels it');
      expect(buttonWith('Check again')).not.toBeNull();
    });
  });

  describe('choosing the keepers', () => {
    it('says what keeping does and does not mean, and when the pictures go', async () => {
      await mount();
      await generate();

      expect(text()).toContain('Nothing is filed in your library on this step');
      expect(text()).toContain('stop being available after');
    });

    it('records a keep on the work itself, so the library step reads it from the server (AF.4.3)', async () => {
      await mount();
      await generate();

      keepBox('img-2').click();
      await settle();

      expect(keepers()).toEqual(['img-2']);
      // A keeper is a picture the work *made*, never one it takes its cues from.
      expect(server.added.map((each) => each.source.purpose)).toEqual(['Keeper']);
      expect(text()).toContain('One picture kept.');
      expect(host.announcements).toContain('Kept.');
      // And nothing about it is kept on this device: the run id is all the draft holds.
      expect(JSON.stringify(host.draft().images)).not.toContain('img-2');
    });

    it('takes a keep back off again, by removing the reference and nothing else', async () => {
      await mount(draftWith(), ['img-1']);
      await generate();

      keepBox('img-1').click();
      await settle();

      expect(keepers()).toEqual([]);
      expect(server.removed.length).toBe(1);
      expect(text()).toContain('Nothing kept yet.');
    });

    it('leaves a mark the run can no longer account for where it is, and says the picture is not there', async () => {
      // Dropping it silently would forget a decision the creator made; the reference points at something no
      // longer usable, which is what a reference means (AF.4.3). The library step is where that is said.
      await mount(draftWith(PROMPT, { operationId: 'op-1' }), ['img-1', 'img-9']);

      watches[0].next({ status: 'found', operation: run('Succeeded', [picture(1), picture(2)]) });
      await settle();

      expect(keepers()).toEqual(['img-1', 'img-9']);
      expect(server.removed).toEqual([]);
    });

    it('says why a keep could not be recorded, and leaves the box as it was', async () => {
      await mount();
      await generate();

      server.refuseAddWith = 'forbidden';
      keepBox('img-2').click();
      await settle();

      expect(keepers()).toEqual([]);
      expect(el.querySelector('[role="alert"]')?.textContent).toContain('do not have permission');
    });
  });

  describe('declining one picture', () => {
    it('asks first, and does nothing on a no', async () => {
      await mount();
      await generate();

      confirmAnswer = false;
      labelled('button', 'Decline picture 1 of 2')!.click();
      await settle();

      expect(confirmCalls.length).toBe(1);
      expect(rejected).toEqual([]);
    });

    it('declines it, says so, and re-reads the run', async () => {
      await mount();
      await generate();
      const before = watches.length;

      labelled('button', 'Decline picture 1 of 2')!.click();
      await settle();

      expect(rejected).toEqual(['cozy-fall|img-1']);
      expect(text()).toContain('Declined.');
      expect(watches.length).withContext('asked again').toBe(before + 1);
    });

    it('takes the mark off a picture it declines, so nothing carries into the next step', async () => {
      await mount(draftWith(), ['img-1']);
      await generate();
      expect(keepers()).toEqual(['img-1']);

      labelled('button', 'Decline picture 1 of 2')!.click();
      await settle();

      expect(keepers()).withContext('a declined picture is not one the creator wants').toEqual([]);
    });

    it('reports a picture the server would not decline, in terms that say why', async () => {
      await mount();
      await generate();
      rejectOutcomes['img-1'] = { status: 'conflict' };

      labelled('button', 'Decline picture 1 of 2')!.click();
      await settle();

      expect(text()).toContain('no longer waiting on a decision');
    });

    it('reports a picture that had already gone', async () => {
      await mount();
      await generate();
      rejectOutcomes['img-1'] = { status: 'not_found' };

      labelled('button', 'Decline picture 1 of 2')!.click();
      await settle();

      expect(text()).toContain('had already gone');
    });
  });

  describe('tidying up', () => {
    it('declines only the pictures that were not kept', async () => {
      await mount();
      await generate(4);
      keepBox('img-2').click();
      await settle();

      await click("Tidy away the ones I didn't keep");

      expect(rejected).toEqual(['cozy-fall|img-1', 'cozy-fall|img-3', 'cozy-fall|img-4']);
      expect(text()).toContain('3 pictures were declined.');
    });

    it('asks first, naming how many it would tidy away, and does nothing on a no', async () => {
      await mount();
      await generate(2);
      confirmAnswer = false;

      await click("Tidy away the ones I didn't keep");

      expect(confirmCalls[0].title).toBe('Tidy away 2 pictures?');
      expect(rejected).toEqual([]);
    });

    it('reports a mixed outcome once per reason rather than once per picture', async () => {
      await mount();
      await generate(3);
      rejectOutcomes['img-2'] = { status: 'not_found' };
      rejectOutcomes['img-3'] = { status: 'not_found' };

      await click("Tidy away the ones I didn't keep");

      expect(text()).toContain('One picture was declined.');
      expect(text()).toContain('had already gone');
      expect(text().match(/had already gone/g)?.length).withContext('said once').toBe(1);
    });

    it('declines in the workspace on screen and names no other', async () => {
      await mount();
      await generate(2);

      await click("Tidy away the ones I didn't keep");

      expect(rejected.every((entry) => entry.startsWith('cozy-fall|'))).toBeTrue();
    });

    it('offers nothing to tidy once everything is either kept or gone', async () => {
      await mount();
      await generate(1);
      keepBox('img-1').click();
      await settle();

      expect(buttonWith("Tidy away the ones I didn't keep")?.disabled).toBeTrue();
      expect(text()).toContain('Nothing left to tidy away.');
    });
  });
});

/**
 * Two workspaces, as `.claude/rules/tenancy.md` requires of every workspace feature.
 *
 * The shell keys this step on the owner, so in the product a change of workspace destroys it rather than
 * re-pointing it. These tests drive the input anyway: a component may not depend on one caller's `track`
 * expression for its isolation, and the poll closure reads the slug input on every request.
 */
describe('ContentPipelineImagesStepComponent across two workspaces', () => {
  beforeEach(() => {
    requests = [];
    watchedSlugs = [];
    watches = [];
    rejected = [];
    rejectOutcomes = {};
    confirmAnswer = true;
    confirmCalls = [];
    requestOutcome = { status: 'unavailable' };
  });

  it('never asks workspace B about workspace A’s run', async () => {
    await mount(draftWith(PROMPT, { operationId: 'op-a' }));
    expect(watchedSlugs).toEqual(['cozy-fall|op-a']);

    watches[0].next({ status: 'found', operation: run('Succeeded', [picture(1), picture(2)]) });
    await settle();
    expect(el.querySelectorAll('.sheet > li').length).toBe(2);

    // The workspace changes and the draft with it, the way a new owner's kept draft would arrive.
    host.workspaceSlug.set('other-kitchen');
    host.draft.set(draftWith(PROMPT, { operationId: 'op-b' }));
    await settle();

    expect(watchedSlugs).toEqual(['cozy-fall|op-a', 'other-kitchen|op-b']);
    // Nothing of A is still on screen while B's run has not answered.
    expect(el.querySelectorAll('.sheet > li').length).toBe(0);
  });

  it('re-points at the new workspace even when the run id is unchanged', async () => {
    await mount(draftWith(PROMPT, { operationId: 'op-1' }));

    host.workspaceSlug.set('other-kitchen');
    await settle();

    expect(watchedSlugs).toEqual(['cozy-fall|op-1', 'other-kitchen|op-1']);
  });

  it('drops A’s pictures when the new workspace has no run of its own', async () => {
    await mount(draftWith(PROMPT, { operationId: 'op-a' }), ['img-1']);
    watches[0].next({ status: 'found', operation: run('Succeeded', [picture(1), picture(2)]) });
    await settle();
    expect(el.querySelectorAll('.sheet > li').length).toBe(2);

    host.workspaceSlug.set('other-kitchen');
    host.draft.set(draftWith(PROMPT));
    await settle();

    expect(el.querySelectorAll('.sheet > li').length).toBe(0);
    expect(text()).not.toContain('Choose the keepers');
    expect(watchedSlugs).withContext('nothing asked of B').toEqual(['cozy-fall|op-a']);
  });

  it('never lets A’s late answer land on the run B is looking at', async () => {
    await mount(draftWith(PROMPT, { operationId: 'op-a' }));

    host.workspaceSlug.set('other-kitchen');
    host.draft.set(draftWith(PROMPT, { operationId: 'op-b' }));
    await settle();

    // A's poll answers after the switch, with two pictures that are not B's.
    watches[0].next({ status: 'found', operation: run('Succeeded', [picture(1), picture(2)]) });
    await settle();

    expect(el.querySelectorAll('.sheet > li').length).withContext('A’s answer dropped').toBe(0);
  });

  it('asks for new pictures in the workspace on screen, not the one it started in', async () => {
    await mount();

    host.workspaceSlug.set('other-kitchen');
    await settle();
    await generate();

    expect(requests.length).toBe(1);
    expect(requests[0].slug).toBe('other-kitchen');
  });

  it('says a run is no longer there without throwing away what the creator chose from it', async () => {
    // The marks are the work's now, and a run going does not unmake a decision: the library step is what
    // accounts for a keeper whose picture has gone (AF.4.3).
    await mount(draftWith(PROMPT, { operationId: 'op-a' }), ['img-1']);

    watches[0].next({ status: 'not_found' });
    await settle();

    expect(text()).toContain('no longer there');
    expect(keepers()).toEqual(['img-1']);
    expect(server.removed).toEqual([]);
  });
});
