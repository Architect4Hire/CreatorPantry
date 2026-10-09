import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { provideRouter } from '@angular/router';
import { EMPTY, Observable, of } from 'rxjs';

import {
  ContentPipelineDraft,
  ContentPipelineImagesState,
  emptyContentPipelineDraft,
} from '../../models/content-pipeline.models';
import { DamAssetDetail, DamCreatedAsset } from '../../models/dam-asset.models';
import {
  GeneratedImageOperationDetail,
  GeneratedImageStatus,
  StagedImage,
} from '../../models/generated-image.models';
import { AiWatchOperationOutcome } from '../../services/ai-request';
import { CreativeContextSession } from '../../services/creative-context-session';
import { FakeCreativeContextService } from '../../services/creative-context.fake';
import { CreativeContextService } from '../../services/creative-context.service';
import { DamAssetDetailOutcome, DamAssetService } from '../../services/dam-asset.service';
import { GeneratedImageService, GeneratedImageWatchOutcome } from '../../services/generated-image.service';
import { ImagePromptService } from '../../services/image-prompt.service';
import { ReferenceImageService } from '../../services/reference-image.service';
import { SaveToLibraryDialogComponent } from '../../shared/save-to-library/save-to-library-dialog.component';
import { ContentPipelineLibraryStepComponent } from './content-pipeline-library-step.component';

const SLUG = 'cozy-fall';
const CTX = 'ctx-1';
const PROMPT = 'Overhead, the loaf torn open on linen.';
const DRAFT_TEXT = 'Overhead shot of a torn loaf on a linen cloth.';

function picture(index: number, overrides: Partial<StagedImage> = {}): StagedImage {
  return {
    id: `img-${index}`,
    variantIndex: index,
    status: 'Staged',
    mediaType: 'image/png',
    width: 1024,
    height: 1024,
    sizeBytes: 482000,
    retentionExpiresAt: '2026-10-12T12:00:00Z',
    createdAt: '2026-10-08T12:00:00Z',
    ...overrides,
  };
}

function operation(images: readonly StagedImage[]): GeneratedImageOperationDetail {
  return {
    id: 'op-1',
    status: 'Succeeded',
    variantCount: images.length,
    stagedCount: images.length,
    providerName: null,
    modelName: null,
    failureCategory: null,
    failureSummary: null,
    requestedAt: '2026-10-08T12:00:00Z',
    completedAt: '2026-10-08T12:01:00Z',
    images,
  };
}

function draftWith(images: Partial<ContentPipelineImagesState> = {}, channelKey: string | null = 'instagram'): ContentPipelineDraft {
  const base = emptyContentPipelineDraft(new Date('2026-10-08T12:00:00Z'));

  return {
    ...base,
    config: { ...base.config, channelKey },
    prompt: {
      ...base.prompt,
      finalPrompt: PROMPT,
      promptSource: 'composed',
      promptRequestId: 'req-1',
      generated: { text: DRAFT_TEXT, avoid: [] },
    },
    images: { operationId: 'op-1', ...images },
    furthestStep: 'library',
  };
}

function createdAsset(overrides: Partial<DamCreatedAsset> = {}): DamCreatedAsset {
  return {
    id: 'a1',
    title: 'Soda bread hero',
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

function assetDetail(id: string, sourceGeneratedImageId: string | null, title: string): DamAssetDetail {
  return {
    id,
    title,
    description: null,
    altText: null,
    kind: 'AiGenerated',
    channelKey: null,
    platformKey: null,
    day: null,
    styleKey: null,
    rightsHolder: null,
    attributionText: null,
    tags: [],
    currentVersion: null,
    versions: [],
    utilizationCount: 0,
    recipeLinks: [],
    brandProfileCount: 0,
    testAttachmentCount: 0,
    prompts: [],
    sourceGeneratedImageId,
    createdAt: '2026-10-09T12:00:00Z',
    updatedAt: '2026-10-09T12:00:00Z',
    concurrencyToken: 'token-1',
  };
}

const PROPOSAL: AiWatchOperationOutcome = {
  status: 'found',
  operation: {
    aiProposalRequestId: 'req-1',
    status: 'Proposed',
    taskType: 'ImagePrompt',
    scope: 'WholeRecipe',
    sourceVersionId: null,
    requestedAt: '2026-10-08T12:00:00Z',
    statusChangedAt: '2026-10-08T12:01:00Z',
    failureCategory: null,
    proposal: {
      proposalId: 'prop-1',
      outputSchemaVersion: '1.0.0',
      promptTemplateId: 'image.prompt',
      promptTemplateVersion: '1.2.0',
      promptTemplateBodyChecksum: 'abc123',
      providerName: 'Foundry',
      modelName: 'gpt-x',
      createdAt: '2026-10-08T12:01:00Z',
      changes: [],
      warnings: [],
    },
  },
};

@Component({
  imports: [ContentPipelineLibraryStepComponent],
  providers: [CreativeContextSession],
  template: `<cp-content-pipeline-library-step
    [workspaceSlug]="slug()"
    [draft]="draft()"
    [session]="session"
    (announced)="announcements.push($event)"
  />`,
})
class HostComponent {
  readonly slug = signal(SLUG);
  readonly draft = signal<ContentPipelineDraft>(draftWith());
  readonly announcements: string[] = [];

  constructor(readonly session: CreativeContextSession) {}
}

let fixture: ComponentFixture<HostComponent>;
let host: HostComponent;
let el: HTMLElement;
let server: FakeCreativeContextService;
let runOutcome: GeneratedImageWatchOutcome;
let details: Record<string, DamAssetDetailOutcome>;
let detailReads: string[];
let promptOutcome: AiWatchOperationOutcome;
let runReads: number;

async function settle(): Promise<void> {
  for (let i = 0; i < 4; i += 1) {
    fixture.detectChanges();
    await fixture.whenStable();
  }
  fixture.detectChanges();
}

/**
 * Mount the step with the keepers the work names and the pictures the run holds.
 *
 * `statuses` is the run's own account of each keeper, which is what decides whether it is already saved.
 */
async function mount(
  keepers: readonly string[] = ['img-1', 'img-2'],
  statuses: readonly GeneratedImageStatus[] = ['Staged', 'Staged'],
  options: { readonly draft?: ContentPipelineDraft; readonly extra?: readonly StagedImage[] } = {},
): Promise<void> {
  server = new FakeCreativeContextService();
  server.seed(SLUG, CTX, {
    workingTitle: 'Soda bread, autumn',
    references: keepers.map((generatedImageId, index) =>
      server.reference({ kind: 'GeneratedImage', purpose: 'Keeper', generatedImageId }, index),
    ),
  });

  runOutcome = {
    status: 'found',
    operation: operation([
      ...keepers.map((id, index) => picture(index + 1, { id, status: statuses[index] ?? 'Staged' })),
      ...(options.extra ?? []),
    ]),
  };

  TestBed.resetTestingModule();
  await TestBed.configureTestingModule({
    providers: [
      provideRouter([]),
      { provide: CreativeContextService, useValue: server },
      {
        provide: GeneratedImageService,
        useValue: {
          watch: (): Observable<GeneratedImageWatchOutcome> => {
            runReads += 1;

            return of(runOutcome);
          },
          preview: () => EMPTY,
        },
      },
      {
        provide: DamAssetService,
        useValue: {
          detail: (slug: string, assetId: string): Observable<DamAssetDetailOutcome> => {
            detailReads.push(`${slug}|${assetId}`);

            return of(details[assetId] ?? { status: 'not_found' });
          },
          keepGeneratedImage: () => Promise.resolve({ status: 'unavailable' as const }),
        },
      },
      { provide: ImagePromptService, useValue: { watch: () => of(promptOutcome) } },
      { provide: ReferenceImageService, useValue: { watch: () => of(promptOutcome) } },
    ],
  }).compileComponents();

  fixture = TestBed.createComponent(HostComponent);
  host = fixture.componentInstance;
  el = fixture.nativeElement;
  if (options.draft) host.draft.set(options.draft);
  await host.session.load(SLUG, CTX);
  await settle();
}

function text(): string {
  return el.textContent ?? '';
}

function rows(): HTMLElement[] {
  return Array.from(el.querySelectorAll('.keepers > li'));
}

function labelled(selector: string, name: string): HTMLElement | null {
  return (
    Array.from(el.querySelectorAll<HTMLElement>(selector)).find((node) => node.getAttribute('aria-label') === name) ??
    null
  );
}

function dialog(): SaveToLibraryDialogComponent | null {
  return fixture.debugElement.query(By.directive(SaveToLibraryDialogComponent))?.componentInstance ?? null;
}

/** Save one keeper the way a creator does: press its button, then let the form answer. */
async function save(position: number, asset: DamCreatedAsset, total = 2): Promise<void> {
  labelled('button', `Save picture ${position} of ${total} to your library`)!.click();
  await settle();

  dialog()!.saved.emit(asset);
  await settle();
}

/** What the work names, as the "server" holds it. */
function references(): readonly { kind: string; purpose: string; id: string | null }[] {
  return (server.stored(SLUG, CTX)?.references ?? []).map((each) => ({
    kind: each.kind,
    purpose: each.purpose,
    id: each.generatedImageId ?? each.mediaAssetId,
  }));
}

describe('ContentPipelineLibraryStepComponent (AF.4.3, FLU-002)', () => {
  beforeEach(() => {
    details = {};
    detailReads = [];
    runReads = 0;
    promptOutcome = PROPOSAL;
  });

  describe('what there is to file', () => {
    it('lists one row per keeper, in the order the creator marked them', async () => {
      await mount();

      expect(rows().length).toBe(2);
      expect(rows().map((row) => row.querySelector('.name')?.textContent?.trim())).toEqual([
        'Picture 1 of 2',
        'Picture 2 of 2',
      ]);
      expect(text()).toContain('0 of 2 in your library.');
    });

    it('shows only the pictures the work kept, not everything the run made', async () => {
      await mount(['img-1'], ['Staged'], { extra: [picture(9, { id: 'img-9' })] });

      expect(rows().length).toBe(1);
      expect(labelled('button', 'Save picture 1 of 1 to your library')).not.toBeNull();
    });

    it('says there is nothing to file when nothing was kept, with the way back', async () => {
      await mount([]);

      expect(text()).toContain("You haven't kept any pictures from this run");
      expect(el.querySelector(`a[href="/${SLUG}/content-pipeline/images"]`)).not.toBeNull();
      expect(dialog()).withContext('no form to open').toBeNull();
    });

    it('states each picture’s format, size on screen and size in bytes', async () => {
      await mount();

      expect(rows()[0].querySelector('.facts')?.textContent).toContain('PNG');
      expect(rows()[0].querySelector('.facts')?.textContent).toContain('482 KB');
    });
  });

  describe('saving each keeper on its own', () => {
    it('opens the form for the picture pressed, with the work’s title and the prompt that made it', async () => {
      await mount();

      labelled('button', 'Save picture 2 of 2 to your library')!.click();
      await settle();

      expect(dialog()?.open()).toBeTrue();
      expect(dialog()?.generatedImageId()).toBe('img-2');
      expect(dialog()?.defaultTitle()).toBe('Soda bread, autumn');
      expect(dialog()?.prompt()?.source).toBe('ImagePromptComposition');
      expect(dialog()?.prompt()?.aiProposalId).toBe('prop-1');
      expect(dialog()?.prompt()?.generatedText).toBe(DRAFT_TEXT);
    });

    it('shows a saved picture as saved, names the asset on the work, and links to it', async () => {
      await mount();

      await save(1, createdAsset());

      expect(rows()[0].textContent).toContain('In your library as “Soda bread hero”.');
      expect(labelled('a', 'Open picture 1 of 2 in your library')?.getAttribute('href')).toBe(`/${SLUG}/dam/a1`);
      expect(references()).toContain({ kind: 'DamAsset', purpose: 'Keeper', id: 'a1' });
      expect(host.announcements).toContain('Saved to your library as “Soda bread hero”.');
      expect(text()).toContain('1 of 2 in your library.');
    });

    it('reaches a clear end state once every keeper is in the library', async () => {
      await mount();

      await save(1, createdAsset());
      await save(2, createdAsset({ id: 'a2', title: 'The second', sourceGeneratedImageId: 'img-2' }));

      expect(text()).toContain('All 2 pictures are in your library.');
      expect(text()).toContain('Everything you kept from this run is in your library.');
      expect(el.querySelector(`a[href="/${SLUG}/dam"]`)).not.toBeNull();
      expect(labelled('button', 'Save picture 1 of 2 to your library')).toBeNull();
      expect(labelled('button', 'Save picture 2 of 2 to your library')).toBeNull();
    });

    it('says a save is running on the picture it is running for', async () => {
      await mount();

      labelled('button', 'Save picture 1 of 2 to your library')!.click();
      await settle();
      dialog()!.progress.emit({ state: 'saving' });
      await settle();

      expect(rows()[0].textContent).toContain('Saving picture 1 of 2 to your library…');
      expect(rows()[1].textContent).withContext('the other is untouched').toContain('Save to library');
    });
  });

  describe('one failure neither undoes nor blocks the others', () => {
    it('leaves the other keepers saveable, and saves them', async () => {
      await mount();

      labelled('button', 'Save picture 1 of 2 to your library')!.click();
      await settle();
      dialog()!.progress.emit({ state: 'failed', problem: 'Nothing was saved.' });
      await settle();
      dialog()!.closed.emit();
      await settle();

      expect(rows()[0].querySelector('[role="alert"]')?.textContent).toContain('Nothing was saved.');
      expect(references().some((each) => each.kind === 'DamAsset')).withContext('nothing written').toBeFalse();

      // The second is unaffected and goes in.
      await save(2, createdAsset({ id: 'a2', title: 'The second', sourceGeneratedImageId: 'img-2' }));

      expect(rows()[1].textContent).toContain('In your library as “The second”.');
      expect(text()).toContain('1 of 2 in your library.');
    });

    it('offers the one that failed another go, with nothing lost', async () => {
      await mount();

      labelled('button', 'Save picture 1 of 2 to your library')!.click();
      await settle();
      dialog()!.progress.emit({ state: 'failed', problem: 'Nothing was saved.' });
      await settle();
      dialog()!.closed.emit();
      await settle();

      labelled('button', 'Try saving picture 1 of 2 again')!.click();
      await settle();

      expect(dialog()?.open()).toBeTrue();
      expect(dialog()?.generatedImageId()).toBe('img-1');
    });

    it('says a picture is in the library even when the work could not be made to point at it', async () => {
      await mount();

      server.refuseAddWith = 'limit';
      await save(1, createdAsset());

      expect(rows()[0].textContent).toContain('In your library as “Soda bread hero”.');
      expect(host.announcements.some((each) => each.includes('could not be made to point at it'))).toBeTrue();
    });
  });

  describe('resuming, and replaying', () => {
    it('retries only what is not saved: the server’s own status is what says so', async () => {
      details['a9'] = { status: 'found', asset: assetDetail('a9', 'img-1', 'Saved last week') };
      server = new FakeCreativeContextService();

      // The first keeper was saved in an earlier visit: its picture is `Kept` and the work names the asset.
      await mount(['img-1', 'img-2'], ['Kept', 'Staged']);
      server.stored(SLUG, CTX);
      await settle();

      expect(rows()[0].textContent).toContain('In your library');
      expect(labelled('button', 'Save picture 1 of 2 to your library')).withContext('already saved').toBeNull();
      expect(labelled('button', 'Save picture 2 of 2 to your library')).withContext('still to do').not.toBeNull();
      expect(text()).toContain('1 of 2 in your library.');
    });

    it('finds the asset of a picture saved before this visit, through the work’s own references', async () => {
      details['a9'] = { status: 'found', asset: assetDetail('a9', 'img-1', 'Saved last week') };
      await mount(['img-1'], ['Kept']);

      server.seed(SLUG, CTX, {
        references: [
          server.reference({ kind: 'GeneratedImage', purpose: 'Keeper', generatedImageId: 'img-1' }, 0),
          server.reference({ kind: 'DamAsset', purpose: 'Keeper', mediaAssetId: 'a9' }, 1),
        ],
      });
      await host.session.load(SLUG, CTX);
      await settle();

      expect(detailReads).toEqual([`${SLUG}|a9`]);
      expect(rows()[0].textContent).toContain('In your library as “Saved last week”.');
      expect(el.querySelector(`a[href="/${SLUG}/dam/a9"]`)).not.toBeNull();
    });

    it('still says a picture is saved when its asset cannot be found, and offers the library', async () => {
      await mount(['img-1'], ['Kept']);

      expect(rows()[0].textContent).toContain('In your library.');
      expect(rows()[0].textContent).not.toContain('In your library as');
      expect(rows()[0].querySelector(`a[href="/${SLUG}/dam"]`)).not.toBeNull();
    });

    it('asks for no asset at all when nothing has been saved', async () => {
      await mount();

      expect(detailReads).toEqual([]);
    });

    it('reads the run again after a save, so the next visit starts from the server’s answer', async () => {
      await mount();
      const before = runReads;

      await save(1, createdAsset());

      // The reference the save added changes what there is to match, which is what re-reads the run.
      expect(runReads).toBeGreaterThan(before);
    });
  });

  describe('a keeper that can no longer be saved', () => {
    it('says a declined picture cannot be saved, and offers no save for it', async () => {
      await mount(['img-1', 'img-2'], ['Rejected', 'Staged']);

      expect(rows()[0].textContent).toContain('was declined, so it cannot be saved');
      expect(labelled('button', 'Save picture 1 of 2 to your library')).toBeNull();
      expect(labelled('button', 'Save picture 2 of 2 to your library')).not.toBeNull();
    });

    it('says an expired picture was held too long, rather than forgetting it was chosen', async () => {
      await mount(['img-1'], ['Expired']);

      expect(rows().length).withContext('the decision is still on the work').toBe(1);
      expect(rows()[0].textContent).toContain('held past the time generated pictures are kept for');
    });

    it('says a picture the run no longer lists is no longer part of it', async () => {
      await mount(['img-1']);
      runOutcome = { status: 'found', operation: operation([]) };
      host.draft.set(draftWith({ operationId: 'op-2' }));
      await settle();

      expect(rows()[0].textContent).toContain('no longer part of the run');
    });

    it('reports a run it could not read, and reads it again on a retry', async () => {
      await mount(['img-1']);
      runOutcome = { status: 'unavailable' };
      host.draft.set(draftWith({ operationId: 'op-2' }));
      await settle();

      // No rows at all: without the run there is nothing truthful to say about any picture's state.
      expect(el.querySelector('[role="alert"]')?.textContent).toContain("couldn't be read just now");
      expect(rows()).toEqual([]);

      runOutcome = { status: 'found', operation: operation([picture(1, { id: 'img-1' })]) };
      (el.querySelector('button') as HTMLButtonElement).click();
      await settle();

      expect(el.querySelector('[role="alert"]')).toBeNull();
      expect(rows().length).toBe(1);
      expect(labelled('button', 'Save picture 1 of 1 to your library')).not.toBeNull();
    });
  });

  describe('the prompt that made them', () => {
    it('is kept with each picture, and says so once rather than per row', async () => {
      await mount();

      expect(text()).toContain('kept with each one');
      expect(dialog()?.prompt()).not.toBeNull();
    });

    it('is not kept without a channel, and says so without stopping the pictures', async () => {
      await mount(['img-1'], ['Staged'], { draft: draftWith({}, null) });

      expect(text()).toContain('has not named one');
      expect(text()).toContain('Your pictures save either way.');
      expect(dialog()?.prompt()).toBeNull();
      expect(labelled('button', 'Save picture 1 of 1 to your library')).not.toBeNull();
    });

    it('is not kept when the generation that wrote it cannot be read, and says so', async () => {
      promptOutcome = { status: 'unavailable' };
      await mount();

      expect(text()).toContain('what wrote it could not be read');
      expect(dialog()?.prompt()).toBeNull();
      expect(labelled('button', 'Save picture 1 of 2 to your library')).not.toBeNull();
    });
  });

  it('files the picture this work made, and leaves the one it takes its cues from alone', async () => {
    // The same picture can be both (AF.4.3): this run's output, and the cue the next prompt is planned from.
    // Only the keeper is something to file.
    server = new FakeCreativeContextService();
    await mount(['img-1'], ['Staged']);
    server.seed(SLUG, CTX, {
      references: [
        server.reference({ kind: 'GeneratedImage', purpose: 'Source', generatedImageId: 'img-2' }, 0),
        server.reference({ kind: 'GeneratedImage', purpose: 'Keeper', generatedImageId: 'img-1' }, 1),
      ],
    });
    await host.session.load(SLUG, CTX);
    await settle();

    expect(rows().length).withContext('the cue is not a keeper').toBe(1);
    expect(labelled('button', 'Save picture 1 of 1 to your library')).not.toBeNull();

    await save(1, createdAsset(), 1);

    // The cue is untouched, and the asset joins the work as a keeper beside it.
    expect(references()).toContain({ kind: 'GeneratedImage', purpose: 'Source', id: 'img-2' });
    expect(references()).toContain({ kind: 'DamAsset', purpose: 'Keeper', id: 'a1' });
  });

  describe('staying inside one workspace', () => {
    it('asks nothing of another workspace when the one on screen changes', async () => {
      await mount();

      host.slug.set('other-kitchen');
      await settle();

      // Every read names the workspace it was made for; none names the other.
      expect(detailReads.every((read) => read.startsWith(`${SLUG}|`))).toBeTrue();
    });

    it('asks nothing at all while the work it holds belongs to another workspace', async () => {
      // The session still has A's context when the screen is pointed at B. Those keeper ids are A's, so
      // asking B about them would be asking one workspace about another's records — and A's keepers must not
      // be drawn under B either, however briefly (.claude/rules/tenancy.md).
      details['a1'] = { status: 'found', asset: assetDetail('a1', 'img-1', 'Workspace A’s picture') };
      await mount(['img-1'], ['Kept']);
      detailReads = [];

      host.slug.set('other-kitchen');
      await settle();

      expect(detailReads).withContext('nothing asked of B about A’s ids').toEqual([]);
      expect(rows()).toEqual([]);
      expect(text()).not.toContain('Workspace A’s picture');
      expect(text()).toContain('Loading the pictures you kept…');
    });
  });
});
