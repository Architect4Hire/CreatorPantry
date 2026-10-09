import { Component, OnInit, input, output, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { NEVER, Observable, Subject, of } from 'rxjs';

import { ConfirmService } from '../../core/confirm.service';
import { AiProposalDetail, AiProposalStatus, AiProposedChange } from '../../models/ai-proposal.models';
import { ContentPipelinePromptState, emptyContentPipelinePromptState } from '../../models/content-pipeline.models';
import { DamAssetDetail, DamAssetSummary } from '../../models/dam-asset.models';
import { GeneratedImageOperationDetail, GeneratedImageStatus, StagedImage } from '../../models/generated-image.models';
import { RequestReferenceImageRequest } from '../../models/reference-image.models';
import { AiRequestOutcome, AiWatchOperationOutcome } from '../../services/ai-request';
import { AiAllowanceState, AiUsageService } from '../../services/ai-usage.service';
import { BrandLibraryOutcome, BrandSourceDocumentService } from '../../services/brand-source-document.service';
import { CreativeContextSession } from '../../services/creative-context-session';
import { FakeCreativeContextService } from '../../services/creative-context.fake';
import { CreativeContextService } from '../../services/creative-context.service';
import { GeneratedImageService, GeneratedImageWatchOutcome } from '../../services/generated-image.service';
import { ReferenceImageService } from '../../services/reference-image.service';
import { DamAssetPickerComponent } from '../dam/dam-asset-picker.component';
import { DamLinkedAssetComponent } from '../dam/dam-linked-asset.component';
import { ContentPipelineReferencePanelComponent } from './content-pipeline-reference-panel.component';

const SLUG = 'cozy-fall';
const CTX = 'ctx-1';
const delay = (ms: number): Promise<void> => new Promise((resolve) => setTimeout(resolve, ms));

/**
 * The library picker, reduced to what the panel uses of it: it is handed a picture when one is chosen. The
 * picker's own searching and paging are its own specs' business.
 */
@Component({
  selector: 'cp-dam-asset-picker',
  template: `
    <button type="button" (click)="choose('a-loaf', 'A loaf I like', 4)">Choose A loaf I like</button>
    <button type="button" (click)="choose('a-table', 'The long table', 1)">Choose The long table</button>
  `,
})
class StubDamAssetPickerComponent {
  readonly workspaceSlug = input.required<string>();
  readonly active = input(true);
  readonly chosen = output<DamAssetSummary>();

  choose(id: string, title: string, currentVersionNumber: number): void {
    this.chosen.emit({ id, title, currentVersionNumber } as DamAssetSummary);
  }
}

/** The linked-asset card, reduced to what it tells its host: the asset, or null when it is not there. */
@Component({
  selector: 'cp-dam-linked-asset',
  template: `<p class="linked">asset {{ assetId() }} at version {{ versionNumber() }}</p>`,
})
class StubDamLinkedAssetComponent implements OnInit {
  readonly workspaceSlug = input.required<string>();
  readonly assetId = input.required<string>();
  readonly versionNumber = input<number | null>(null);
  readonly resolved = output<DamAssetDetail | null>();

  ngOnInit(): void {
    this.resolved.emit(this.assetId() === 'a-gone' ? null : ({ id: this.assetId() } as DamAssetDetail));
  }
}

@Component({
  imports: [ContentPipelineReferencePanelComponent],
  providers: [CreativeContextSession],
  template: `<cp-content-pipeline-reference-panel
    [workspaceSlug]="slug"
    [prompt]="prompt()"
    [session]="session"
    [runOperationId]="runOperationId()"
    (changed)="apply($event)"
    (announced)="announcements.push($event)"
  />`,
})
class HostComponent {
  readonly slug = SLUG;
  readonly prompt = signal<ContentPipelinePromptState>(emptyContentPipelinePromptState());
  readonly runOperationId = signal<string | null>(null);
  readonly announcements: string[] = [];

  constructor(readonly session: CreativeContextSession) {}

  apply(next: ContentPipelinePromptState): void {
    this.prompt.set(next);
  }
}

function row(overrides: Partial<AiProposedChange>): AiProposedChange {
  return {
    changeId: 'c1',
    changeKind: 'Set',
    targetKind: 'ReferenceImageAnalysis',
    targetId: 't-1',
    fieldName: null,
    beforeValue: null,
    afterValue: null,
    proposedPosition: null,
    disposition: 'Pending',
    ...overrides,
  };
}

const READING: AiProposalDetail = {
  proposalId: 'p-1',
  outputSchemaVersion: '1',
  promptTemplateId: 'image.reference-analysis',
  promptTemplateVersion: '1.1.0',
  promptTemplateBodyChecksum: 'abc',
  providerName: 'provider',
  modelName: 'model',
  createdAt: '2026-10-09T12:00:05Z',
  changes: [
    row({ changeKind: 'Add', afterValue: 'A loaf on scrubbed oak in side light.', proposedPosition: 0 }),
    row({ fieldName: 'observation.Lighting', afterValue: 'Hard side light from the left' }),
    row({ fieldName: 'observation.Lighting.confidence', afterValue: 'Clear' }),
  ],
  warnings: [],
};

function operation(detail: AiProposalDetail | null, status: AiProposalStatus['status']): AiProposalStatus {
  return {
    aiProposalRequestId: 'r-ref',
    status,
    taskType: 'ReferenceImageAnalysis',
    scope: 'Unspecified',
    sourceVersionId: null,
    requestedAt: '2026-10-09T12:00:00Z',
    statusChangedAt: '2026-10-09T12:00:05Z',
    failureCategory: null,
    proposal: detail,
  };
}

function staged(index: number, status: GeneratedImageStatus = 'Staged'): StagedImage {
  return {
    id: `img-${index}`,
    variantIndex: index,
    status,
    mediaType: 'image/png',
    width: 1024,
    height: 1024,
    sizeBytes: 482000,
    retentionExpiresAt: '2026-10-10T12:00:00Z',
    createdAt: '2026-10-09T12:00:00Z',
  };
}

function run(images: readonly StagedImage[]): GeneratedImageOperationDetail {
  return {
    id: 'op-1',
    status: 'Succeeded',
    variantCount: images.length,
    stagedCount: images.length,
    providerName: 'provider',
    modelName: 'image-model',
    failureCategory: null,
    failureSummary: null,
    requestedAt: '2026-10-09T12:00:00Z',
    completedAt: '2026-10-09T12:01:00Z',
    images,
  };
}

describe('ContentPipelineReferencePanelComponent — choosing a picture from three places', () => {
  let fixture: ComponentFixture<HostComponent>;
  let host: HostComponent;
  let el: HTMLElement;
  let server: FakeCreativeContextService;
  let requests: { slug: string; request: RequestReferenceImageRequest; key: string }[];
  let readings: Subject<AiWatchOperationOutcome>[];
  let requestOutcome: AiRequestOutcome;
  let runWatches: string[];
  let runOutcome: GeneratedImageWatchOutcome;
  let confirmAnswer: boolean;

  async function settle(): Promise<void> {
    for (let i = 0; i < 4; i += 1) {
      fixture.detectChanges();
      await delay(0);
    }
    fixture.detectChanges();
  }

  async function mount(
    options: { references?: Parameters<FakeCreativeContextService['reference']>[0][]; runOperationId?: string | null } = {},
  ): Promise<void> {
    server.seed(SLUG, CTX, { references: (options.references ?? []).map((source, index) => server.reference(source, index)) });

    fixture = TestBed.createComponent(HostComponent);
    host = fixture.componentInstance;
    host.runOperationId.set(options.runOperationId ?? null);
    el = fixture.nativeElement;
    await host.session.load(SLUG, CTX);
    await settle();
  }

  function buttonWith(text: string): HTMLButtonElement | null {
    return (
      Array.from(el.querySelectorAll('button')).find((button) => (button.textContent ?? '').trim().startsWith(text)) ??
      null
    );
  }

  async function click(text: string): Promise<void> {
    buttonWith(text)!.click();
    await settle();
  }

  async function openTab(label: string): Promise<void> {
    Array.from(el.querySelectorAll<HTMLElement>('[role="tab"]'))
      .find((tab) => tab.textContent?.trim() === label)!
      .click();
    await settle();
  }

  function references() {
    return server.stored(SLUG, CTX)!.references;
  }

  async function answerReading(detail: AiProposalDetail | null, status: AiProposalStatus['status'] = 'Proposed'): Promise<void> {
    readings[readings.length - 1].next({ status: 'found', operation: operation(detail, status) });
    await settle();
  }

  beforeEach(() => {
    server = new FakeCreativeContextService();
    requests = [];
    readings = [];
    runWatches = [];
    confirmAnswer = true;
    requestOutcome = { status: 'accepted', operation: operation(null, 'Requested'), replayed: false };
    runOutcome = { status: 'found', operation: run([staged(1), staged(2, 'Rejected'), staged(3, 'Kept'), staged(4, 'Expired')]) };

    TestBed.configureTestingModule({
      providers: [
        { provide: CreativeContextService, useValue: server },
        {
          provide: ReferenceImageService,
          useValue: {
            request: (slug: string, request: RequestReferenceImageRequest, key: string) => {
              requests.push({ slug, request, key });
              return Promise.resolve(requestOutcome);
            },
            watch: (): Observable<AiWatchOperationOutcome> => {
              const subject = new Subject<AiWatchOperationOutcome>();
              readings.push(subject);
              return subject.asObservable();
            },
          },
        },
        {
          provide: GeneratedImageService,
          useValue: {
            watch: (slug: string, operationId: string): Observable<GeneratedImageWatchOutcome> => {
              runWatches.push(`${slug}|${operationId}`);
              return of(runOutcome);
            },
            // Never answers: these specs are about choosing a picture, not about showing its pixels.
            preview: () => NEVER,
          },
        },
        {
          provide: AiUsageService,
          useValue: { allowance: signal<AiAllowanceState>({ kind: 'unknown' }), ensureLoaded: () => Promise.resolve() },
        },
        { provide: ConfirmService, useValue: { confirm: () => Promise.resolve(confirmAnswer) } },
        {
          provide: BrandSourceDocumentService,
          useValue: {
            searchLibrary: (): Observable<BrandLibraryOutcome> => of({ status: 'ok', page: { items: [], nextCursor: null } }),
            upload: () => Promise.resolve({ status: 'failed' as const, reason: 'unavailable' as const }),
          },
        },
      ],
    });
    TestBed.overrideComponent(ContentPipelineReferencePanelComponent, {
      remove: { imports: [DamAssetPickerComponent, DamLinkedAssetComponent] },
      add: { imports: [StubDamAssetPickerComponent, StubDamLinkedAssetComponent] },
    });
  });

  describe('the chooser', () => {
    it('offers the three places as tabs, named, and opens on the library', async () => {
      await mount();

      const tabs = Array.from(el.querySelectorAll('[role="tab"]')).map((tab) => tab.textContent?.trim());
      expect(tabs).toEqual(['My library', "This run's pictures", 'Brand Library']);
      expect(el.querySelector('[role="tablist"]')?.getAttribute('aria-label')).toBe('Where to choose a reference picture from');
      expect(el.querySelector('[role="tab"][aria-selected="true"]')?.textContent?.trim()).toBe('My library');
      expect(buttonWith('Choose A loaf I like')).not.toBeNull();
    });

    it('offers no reading and asks for nothing until a picture is chosen', async () => {
      await mount();

      expect(buttonWith('Read this picture')).toBeNull();
      expect(requests.length).toBe(0);
      expect(server.added.length).toBe(0);
    });

    it('still offers the Brand Library, with its own picker', async () => {
      await mount();
      await openTab('Brand Library');

      expect(el.querySelector('cp-content-pipeline-document-picker')).not.toBeNull();
    });
  });

  describe('a picture from the library', () => {
    it('is stored on the context as a reference, pinned to the version it is at', async () => {
      await mount();

      await click('Choose A loaf I like');

      expect(references().map((each) => [each.kind, each.mediaAssetId, each.mediaAssetVersionNumber])).toEqual([
        ['DamAsset', 'a-loaf', 4],
      ]);
      expect(server.added[0].slug).toBe(SLUG);
      expect(host.announcements).toContain('A loaf I like is your reference picture.');
    });

    it('moves focus to the summary it becomes, since the Choose button is gone, and names that summary', async () => {
      await mount();

      await click('Choose A loaf I like');

      const summary = el.querySelector<HTMLElement>('.chosen')!;
      expect(document.activeElement).toBe(summary);
      expect(summary.getAttribute('aria-label')).toBe('Your reference picture');
      expect(summary.getAttribute('tabindex')).withContext('focusable by script, never a tab stop').toBe('-1');
    });

    it('is shown as a reference for inspiration, and never with a way to treat it as a result', async () => {
      await mount();
      await click('Choose A loaf I like');

      expect(el.querySelector('.chosen')?.textContent).toContain('Reference');
      expect(el.querySelector('.chosen')?.textContent).toContain('For inspiration only');
      expect(el.querySelector('.linked')?.textContent).toContain('asset a-loaf at version 4');
      expect(el.querySelector('[role="tablist"]')).withContext('one picture at a time').toBeNull();
      expect(buttonWith('Keep')).toBeNull();
      expect(buttonWith('Download')).toBeNull();
    });

    it('is sent through the reading request by its source, id and pinned version', async () => {
      await mount();
      await click('Choose A loaf I like');

      await click('Read this picture');

      expect(requests.length).toBe(1);
      expect(requests[0].slug).toBe(SLUG);
      expect(requests[0].request).toEqual({
        picture: { source: 'DamAsset', mediaAssetId: 'a-loaf', mediaAssetVersionNumber: 4 },
        note: '',
      });
      expect(host.prompt().referenceRequestId).toBe('r-ref');
    });

    it('is read back from the context when the work is opened again', async () => {
      await mount({ references: [{ kind: 'DamAsset', mediaAssetId: 'a-table', mediaAssetVersionNumber: 2 }] });

      expect(el.querySelector('.linked')?.textContent).toContain('asset a-table at version 2');
      expect(buttonWith('Read this picture')).not.toBeNull();
    });

    it('says so when it is no longer in the library, and offers no reading of it', async () => {
      await mount({ references: [{ kind: 'DamAsset', mediaAssetId: 'a-gone', mediaAssetVersionNumber: 1 }] });

      expect(el.textContent).toContain('This picture is no longer available');
      expect(buttonWith('Read this picture')).toBeNull();
      expect(buttonWith('Remove')).not.toBeNull();
    });
  });

  describe("a picture from this run", () => {
    it('says there are none to choose yet when the run has made no pictures', async () => {
      await mount();
      await openTab("This run's pictures");

      expect(el.textContent).toContain('No pictures have been made in this run yet');
      expect(runWatches).toEqual([]);
    });

    it('asks for the run’s pictures only once its tab is opened, in this workspace', async () => {
      await mount({ runOperationId: 'op-1' });
      expect(runWatches).withContext('a panel nobody has opened asks for nothing').toEqual([]);

      await openTab("This run's pictures");

      expect(runWatches).toEqual([`${SLUG}|op-1`]);
    });

    it('offers the pictures that are still the creator’s, and none that were declined or expired', async () => {
      await mount({ runOperationId: 'op-1' });
      await openTab("This run's pictures");

      const choices = Array.from(el.querySelectorAll('.run-pictures button')).map((button) => button.getAttribute('aria-label'));
      expect(choices).toEqual(['Choose picture 1 of 2 as the reference', 'Choose picture 2 of 2 as the reference']);
    });

    it('is stored on the context as a generated-image reference and sent by its source and id', async () => {
      await mount({ runOperationId: 'op-1' });
      await openTab("This run's pictures");

      (el.querySelectorAll<HTMLButtonElement>('.run-pictures button')[1]).click();
      await settle();

      expect(references().map((each) => [each.kind, each.generatedImageId])).toEqual([['GeneratedImage', 'img-3']]);
      expect(el.querySelector('.chosen-name')?.textContent).toContain('Picture 2 from this run');
      expect(host.announcements).toContain('Picture 2 from this run is your reference picture.');

      await click('Read this picture');

      expect(requests[0].request).toEqual({ picture: { source: 'GeneratedImage', generatedImageId: 'img-3' }, note: '' });
    });

    it('says the run’s pictures are no longer available when none are left', async () => {
      runOutcome = { status: 'found', operation: run([staged(1, 'Rejected'), staged(2, 'Expired')]) };
      await mount({ runOperationId: 'op-1' });
      await openTab("This run's pictures");

      expect(el.textContent).toContain('The pictures from this run are no longer available');
    });

    it('says they could not be loaded, and loads them on a retry', async () => {
      runOutcome = { status: 'unavailable' };
      await mount({ runOperationId: 'op-1' });
      await openTab("This run's pictures");

      expect(el.textContent).toContain("This run's pictures couldn't be loaded right now");

      runOutcome = { status: 'found', operation: run([staged(1)]) };
      await click('Try again');

      expect(el.querySelectorAll('.run-pictures button').length).toBe(1);
    });
  });

  describe('one picture at a time', () => {
    it('replaces the chosen picture rather than adding a second, across sources', async () => {
      await mount({ runOperationId: 'op-1' });
      await click('Choose A loaf I like');
      await click('Remove');
      await openTab("This run's pictures");
      (el.querySelectorAll<HTMLButtonElement>('.run-pictures button')[0]).click();
      await settle();

      expect(references().map((each) => each.kind)).toEqual(['GeneratedImage']);
    });

    it('lets go of a picture a hand-off put on the context when another is chosen through the session', async () => {
      await mount({ references: [{ kind: 'GeneratedImage', generatedImageId: 'img-handed-over' }] });

      expect(el.querySelector('.chosen-name')?.textContent).toContain('A generated picture');

      await click('Remove');
      await click('Choose The long table');

      expect(references().map((each) => [each.kind, each.mediaAssetId])).toEqual([['DamAsset', 'a-table']]);
    });
  });

  describe('removing', () => {
    it('unlinks the picture from the context, forgets its reading, and offers the chooser again', async () => {
      await mount();
      await click('Choose A loaf I like');
      await click('Read this picture');
      await answerReading(READING);
      expect(el.textContent).toContain('Hard side light from the left');

      await click('Remove');

      expect(references()).toEqual([]);
      expect(host.prompt().referenceRequestId).toBeNull();
      expect(el.textContent).not.toContain('Hard side light from the left');
      expect(el.querySelector('[role="tablist"]')).not.toBeNull();
      expect(host.announcements).toContain('The reference picture is removed.');
    });

    it('moves focus to the chooser that replaces the summary, since the button pressed is gone', async () => {
      await mount();
      await click('Choose A loaf I like');

      await click('Remove');

      expect(document.activeElement).toBe(el.querySelector('[role="tab"][aria-selected="true"]'));
    });

    it('names what Remove removes, so the button reads alone', async () => {
      await mount();
      await click('Choose A loaf I like');

      expect(buttonWith('Remove')!.getAttribute('aria-label')).toBe('Remove the reference picture');
    });
  });

  describe('the reading', () => {
    it('shows what the analysis said, with how sure it is of each part', async () => {
      await mount();
      await click('Choose A loaf I like');
      await click('Read this picture');
      await answerReading(READING);

      expect(el.textContent).toContain('What the picture shows');
      expect(el.textContent).toContain('Hard side light from the left');
      expect(el.querySelector('.confidence')?.textContent?.trim()).toBeTruthy();
    });

    it('heads the reading one level below its host, at the level the host says', async () => {
      await mount();
      await click('Choose A loaf I like');
      await click('Read this picture');
      await answerReading(READING);

      const levels = Array.from(el.querySelectorAll('[role="heading"]')).map((heading) => heading.getAttribute('aria-level'));
      expect(levels).toEqual(['5', '5']);
      expect(el.querySelector('h4')).withContext('no heading that repeats the host’s level').toBeNull();
    });

    it('shapes nothing until the creator says so', async () => {
      await mount();
      await click('Choose A loaf I like');
      await click('Read this picture');
      await answerReading(READING);

      expect(host.prompt().finalPrompt).toBe('');
      expect(host.prompt().promptSource).toBe('none');
    });

    it('can be discarded before it shapes a prompt, leaving the picture chosen and the prompt untouched', async () => {
      await mount();
      host.prompt.set({ ...host.prompt(), finalPrompt: 'My own prompt.', promptSource: 'creator' });
      await click('Choose A loaf I like');
      await click('Read this picture');
      await answerReading(READING);

      await click('Discard this reading');

      expect(el.textContent).not.toContain('Hard side light from the left');
      expect(buttonWith('Use this as my prompt')).toBeNull();
      expect(host.prompt().referenceRequestId).toBeNull();
      expect(host.prompt().finalPrompt).toBe('My own prompt.');
      expect(references().length).withContext('the picture is still chosen').toBe(1);
      expect(buttonWith('Read this picture')).not.toBeNull();
      expect(host.announcements).toContain('The reading is discarded. Nothing was taken from it.');
    });

    it('takes a new key after a discard, so reading again is a new reading', async () => {
      await mount();
      await click('Choose A loaf I like');
      await click('Read this picture');
      await answerReading(READING);
      await click('Discard this reading');

      await click('Read this picture');

      expect(requests.length).toBe(2);
      expect(requests[1].key).not.toBe(requests[0].key);
    });
  });

  describe('when the analysis fails', () => {
    it('says a picture the server will not read cannot be read, and what to do instead', async () => {
      await mount({ runOperationId: 'op-1' });
      await openTab("This run's pictures");
      (el.querySelectorAll<HTMLButtonElement>('.run-pictures button')[0]).click();
      await settle();
      requestOutcome = { status: 'refused', code: 'ai.referenceImage.not_found', message: 'This workspace has no image with that id.' };

      await click('Read this picture');

      expect(el.querySelector('[role="alert"]')?.textContent).toContain('This workspace has no image with that id.');
      expect(el.textContent).toContain('Remove this picture and choose a different one');
      expect(host.prompt().referenceRequestId).toBeNull();
    });

    it('keeps the picture chosen when the server cannot be reached, and asks again under the same key', async () => {
      await mount();
      await click('Choose A loaf I like');
      requestOutcome = { status: 'unavailable' };

      await click('Read this picture');

      expect(el.textContent).toContain('That could not be asked for just now');
      expect(references().length).toBe(1);

      requestOutcome = { status: 'accepted', operation: operation(null, 'Requested'), replayed: false };
      await click('Try again');

      expect(requests.length).toBe(2);
      expect(requests[1].key).withContext('a retry replays; it does not buy a second reading').toBe(requests[0].key);
      expect(host.prompt().referenceRequestId).toBe('r-ref');
    });

    it('says a reading that failed afterwards did, and offers to try again', async () => {
      await mount();
      await click('Choose A loaf I like');
      await click('Read this picture');

      await answerReading(null, 'Failed');

      expect(buttonWith('Try again')).not.toBeNull();
      expect(el.textContent).not.toContain('What the picture shows');
    });
  });

  describe('when choosing fails', () => {
    it('chooses nothing, says so, and chooses on a retry', async () => {
      await mount();
      server.offline = true;

      await click('Choose A loaf I like');

      expect(el.querySelector('[role="alert"]')?.textContent).toContain("couldn't be done right now");
      expect(references()).toEqual([]);
      expect(el.querySelector('.chosen')).toBeNull();

      server.offline = false;
      await click('Try again');

      expect(references().map((each) => each.mediaAssetId)).toEqual(['a-loaf']);
    });

    it('says a picture that can no longer be named cannot be chosen', async () => {
      await mount({ runOperationId: 'op-1' });
      await openTab("This run's pictures");
      server.refuseAddWith = 'source_unavailable';

      (el.querySelectorAll<HTMLButtonElement>('.run-pictures button')[0]).click();
      await settle();

      expect(el.querySelector('[role="alert"]')?.textContent).toContain('can no longer be chosen');
      expect(references()).toEqual([]);
    });
  });
});
