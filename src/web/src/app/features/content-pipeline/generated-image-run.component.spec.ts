import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { provideRouter } from '@angular/router';
import { EMPTY, Observable, Subject, of } from 'rxjs';

import { ConfirmService } from '../../core/confirm.service';
import { ContentPipelineImagesState } from '../../models/content-pipeline.models';
import { CreativeContextReference } from '../../models/creative-context.models';
import { DamAssetDetail, DamCreatedAsset } from '../../models/dam-asset.models';
import {
  GeneratedImageOperationDetail,
  GeneratedImageQuote,
  GeneratedImageSubject,
  RequestGeneratedImagesRequest,
  StagedImage,
} from '../../models/generated-image.models';
import { DamAssetDetailOutcome, DamAssetService } from '../../services/dam-asset.service';
import {
  GeneratedImageRequestOutcome,
  GeneratedImageService,
  GeneratedImageWatchOutcome,
} from '../../services/generated-image.service';
import { RecipeService } from '../../services/recipe.service';
import { WorkspaceTagService } from '../../services/workspace-tag.service';
import { SaveToLibraryDialogComponent } from '../../shared/save-to-library/save-to-library-dialog.component';
import {
  GeneratedImageRunComponent,
  GeneratedImageRunLibrary,
  GeneratedImageRunWording,
  PIPELINE_IMAGE_RUN_WORDING,
  QUOTE_SETTLE_MS,
} from './generated-image-run.component';

// What a run does — asking, watching, choosing, declining, tidying up, and staying inside one workspace — is
// covered through its first caller in `content-pipeline-images-step.component.spec.ts`, which drives this
// component through a one-line wrapper. This file covers only what that caller never varies: the inputs a
// second caller sets, and the library a filing surface hands over (AF.4.2).

const ELSEWHERE: GeneratedImageRunWording = {
  makeIntro: 'The prompt above, as it will be sent.',
  noPrompt: 'Write a prompt above first.',
  tooLongRemedy: 'Shorten it above.',
  countRemedy: 'Change that in the brief.',
  forbidden: 'Ask an Owner to make them.',
  chooseHeading: 'Save the ones worth keeping',
  chooseIntro: 'Saving puts a picture in your library for good.',
  keepNote: 'A saved picture is yours from then on.',
  expiryRemedy: 'so save anything you want to keep.',
};

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

function damReference(id: string, mediaAssetId: string, sortOrder: number): CreativeContextReference {
  return {
    id,
    kind: 'DamAsset',
    purpose: 'Keeper',
    sortOrder,
    recipeId: null,
    recipeVersionId: null,
    conceptRequestId: null,
    conceptId: null,
    mediaAssetId,
    mediaAssetVersionNumber: 1,
    generatedImageId: null,
    promptRecordId: null,
    addedAt: '2026-10-09T12:00:00Z',
  };
}

@Component({
  imports: [GeneratedImageRunComponent],
  template: `<cp-generated-image-run
    workspaceSlug="cozy-fall"
    [promptText]="promptText()"
    [avoidText]="avoidText()"
    [variantCount]="variantCount()"
    [subject]="subject()"
    [state]="state()"
    [wording]="wording()"
    [sectionIdPrefix]="prefix()"
    [library]="library()"
    [keepers]="keepers()"
    (changed)="changes.push($event); state.set($event)"
    (keepToggled)="keeps.push($event)"
    (announced)="announcements.push($event)"
    (saved)="saves.push($event)"
  />`,
})
class HostComponent {
  readonly promptText = signal('A tight crop.');
  readonly avoidText = signal<string | null>(null);
  readonly variantCount = signal(2);
  readonly subject = signal<GeneratedImageSubject | null>(null);
  readonly state = signal<ContentPipelineImagesState>({ operationId: null });
  readonly wording = signal<GeneratedImageRunWording>(ELSEWHERE);
  readonly prefix = signal('cp-studio-images');
  readonly library = signal<GeneratedImageRunLibrary | null>(null);
  readonly keepers = signal<readonly string[]>([]);

  readonly keeps: { readonly id: string; readonly keep: boolean }[] = [];
  readonly changes: ContentPipelineImagesState[] = [];
  readonly announcements: string[] = [];
  readonly saves: DamCreatedAsset[] = [];
}

let fixture: ComponentFixture<HostComponent>;
let host: HostComponent;
let el: HTMLElement;
let requests: RequestGeneratedImagesRequest[];
let keys: string[];
let requestOutcome: GeneratedImageRequestOutcome;
let watches: Subject<GeneratedImageWatchOutcome>[];
let details: Record<string, DamAssetDetailOutcome>;
let detailReads: string[];
let quoteAsks: number[];
let quoteAnswer: (variants: number) => GeneratedImageQuote | null;

/** A price the server might give: affordable by default, 50 credits a picture, from an allowance of 1,000. */
function quote(variants: number, overrides: Partial<GeneratedImageQuote> = {}): GeneratedImageQuote {
  return {
    outcome: 'Affordable',
    unit: 'Credits',
    variants,
    perPicture: 50,
    total: variants * 50,
    allowance: 1000,
    remaining: 1000,
    remainingAfter: 1000 - variants * 50,
    affordableVariants: variants,
    leavesNearlySpent: false,
    resetsAt: '2026-11-01T00:00:00+00:00',
    timeZoneId: 'Etc/UTC',
    ...overrides,
  };
}

/** Waits out the quote's settle time, then lets the answer render. */
async function priced(): Promise<void> {
  await settle();
  await new Promise((resolve) => setTimeout(resolve, QUOTE_SETTLE_MS + 20));
  await settle();
}

/**
 * Answers the next quote with `next`. The run asked for its first price while the suite was being set up, so this
 * nudges the count away and straight back inside one settle window — one ask, for the same count — and waits.
 */
async function answerWith(next: (variants: number) => GeneratedImageQuote | null): Promise<void> {
  quoteAnswer = next;
  const count = host.variantCount();
  host.variantCount.set(count + 1);
  fixture.detectChanges();
  host.variantCount.set(count);
  await priced();
}

function quoteLine(): string {
  return (el.querySelector('[id$="-quote"]')?.textContent ?? '').trim();
}

async function settle(): Promise<void> {
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
}

function makeButton(): HTMLButtonElement {
  return Array.from(el.querySelectorAll('button')).find(
    (button) => (button.textContent ?? '').trim() === 'Make the pictures',
  ) as HTMLButtonElement;
}

function tiles(): HTMLElement[] {
  return Array.from(el.querySelectorAll('.sheet > li:not(.pending)'));
}

function labelled(selector: string, name: string): HTMLElement | null {
  return (
    Array.from(el.querySelectorAll<HTMLElement>(selector)).find((node) => node.getAttribute('aria-label') === name) ??
    null
  );
}

/** The save form the run mounts, so the run's handling of its answers can be driven without filling it in. */
function dialog(): SaveToLibraryDialogComponent | null {
  return fixture.debugElement.query(By.directive(SaveToLibraryDialogComponent))?.componentInstance ?? null;
}

/** Arrive with a finished run on screen, which is how a creator comes back to one. */
async function arrive(images: readonly StagedImage[]): Promise<void> {
  host.state.set({ operationId: 'op-1' });
  await settle();

  watches[watches.length - 1].next({ status: 'found', operation: operation(images) });
  await settle();
}

describe('GeneratedImageRunComponent', () => {
  beforeEach(async () => {
    requests = [];
    keys = [];
    watches = [];
    details = {};
    detailReads = [];
    quoteAsks = [];
    quoteAnswer = (variants) => quote(variants);
    requestOutcome = { status: 'unavailable' };

    await TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        {
          provide: GeneratedImageService,
          useValue: {
            request: (_slug: string, request: RequestGeneratedImagesRequest, key: string) => {
              requests.push(request);
              keys.push(key);

              return Promise.resolve(requestOutcome);
            },
            watch: (): Observable<GeneratedImageWatchOutcome> => {
              const subject = new Subject<GeneratedImageWatchOutcome>();
              watches.push(subject);

              return subject.asObservable();
            },
            preview: () => EMPTY,
            reject: () => Promise.resolve({ status: 'declined' as const }),
            downloadUrl: () => null,
            quote: (_slug: string, variants: number) => {
              quoteAsks.push(variants);
              return Promise.resolve(quoteAnswer(variants));
            },
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
        { provide: RecipeService, useValue: { searchRecipes: () => EMPTY } },
        { provide: WorkspaceTagService, useValue: { list: () => Promise.resolve({ status: 'unavailable' as const }) } },
        { provide: ConfirmService, useValue: { confirm: () => Promise.resolve(true) } },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(HostComponent);
    host = fixture.componentInstance;
    el = fixture.nativeElement;
    await settle();
  });

  it("says its caller's sentences rather than the pipeline's", async () => {
    expect(el.textContent).toContain(ELSEWHERE.makeIntro);
    expect(el.textContent).toContain('Asking for 2 pictures. Change that in the brief.');
    expect(el.textContent).not.toContain(PIPELINE_IMAGE_RUN_WORDING.countRemedy);

    host.promptText.set('   ');
    await settle();

    expect(el.textContent).toContain(ELSEWHERE.noPrompt);
    expect(makeButton().disabled).toBeTrue();
  });

  it('says in plain words why nothing was made when pictures are not switched on', async () => {
    // Every surface that makes pictures reaches this component — the Image Studio and the "Create food images"
    // workflow step through it — so this is where "the provider is not configured" has to become a sentence a
    // creator can act on. Nothing asserted that it does, on any of those surfaces.
    host.state.set({ operationId: 'op-1' });
    await settle();

    watches[watches.length - 1].next({
      status: 'found',
      operation: { ...operation([]), status: 'Failed', failureCategory: 'provider-not-configured', completedAt: null },
    });
    await settle();

    expect(el.textContent).toContain('Making pictures is not switched on');

    // And no provider, model, lease or queue vocabulary reaches the creator with it.
    expect(el.textContent).not.toContain('provider-not-configured');
  });

  it('says where to shorten a prompt that is too long to send, and will not send it', async () => {
    host.promptText.set('x'.repeat(4001));
    await settle();

    expect(el.querySelector('[role="alert"]')?.textContent).toContain('4001 of 4000 characters. Shorten it above.');
    expect(makeButton().disabled).toBeTrue();
  });

  it("tells a refused member so in its caller's words", async () => {
    requestOutcome = { status: 'forbidden' };

    makeButton().click();
    await settle();

    expect(el.querySelector('[role="alert"]')?.textContent).toContain(ELSEWHERE.forbidden);
  });

  it('names its sections with the prefix it was given, so a caller can navigate to them', () => {
    expect(el.querySelector('#cp-studio-images-make')).not.toBeNull();
    expect(el.querySelector('#cp-pipeline-images-make')).toBeNull();
  });

  it('sends the prompt trimmed, the avoid list it was handed, and a count held within what a run allows', async () => {
    host.promptText.set('  A tight crop.  ');
    host.avoidText.set('clutter, harsh light');
    host.variantCount.set(9);
    await settle();

    makeButton().click();
    await settle();

    expect(requests).toEqual([
      { promptText: 'A tight crop.', avoidText: 'clutter, harsh light', variantCount: 4, subject: null },
    ]);
  });

  it('says what the pictures are for when the surface handed that in', async () => {
    const subject = { recipeId: 'r1', recipeVersionId: 'v2', creativeContextId: 'ctx-a' };
    host.subject.set(subject);
    await settle();

    makeButton().click();
    await settle();

    expect(requests[0].subject).toEqual(subject);
  });

  describe('one key, one request (12.10l)', () => {
    function askButton(): HTMLButtonElement {
      return Array.from(el.querySelectorAll('button')).find((button) =>
        ['Make the pictures', 'Try again'].includes((button.textContent ?? '').trim()),
      ) as HTMLButtonElement;
    }

    async function ask(): Promise<void> {
      askButton().click();
      await settle();
    }

    it('repeats an unanswered ask under the same key, so a lost answer is a replay and not a second charge', async () => {
      await ask();
      await ask();

      expect(keys.length).toBe(2);
      expect(keys[1]).toBe(keys[0]);
    });

    it('takes a new key once what is asked for has changed, since the server refuses the old one for it', async () => {
      await ask();

      host.promptText.set('A wider crop, from above.');
      await settle();
      await ask();

      expect(requests.map((request) => request.promptText)).toEqual(['A tight crop.', 'A wider crop, from above.']);
      expect(keys[1]).not.toBe(keys[0]);

      // And the new ask keeps its own key across its own retry.
      await ask();
      expect(keys[2]).toBe(keys[1]);
    });

    it('takes a new key when only the number of pictures changed', async () => {
      await ask();

      host.variantCount.set(3);
      await settle();
      await ask();

      expect(keys[1]).not.toBe(keys[0]);
    });
  });

  it('defaults to the pipeline wording, so its first caller passes none', () => {
    host.wording.set(PIPELINE_IMAGE_RUN_WORDING);
    fixture.detectChanges();

    expect(el.textContent).toContain('Change that on the first step.');
  });

  describe('the pictures as a set (AU.5.2)', () => {
    async function running(images: readonly StagedImage[], asked: number): Promise<void> {
      host.state.set({ operationId: 'op-1' });
      await settle();

      watches[watches.length - 1].next({
        status: 'found',
        operation: { ...operation(images), status: 'Running', variantCount: asked, stagedCount: images.length, completedAt: null },
      });
      await settle();
    }

    it('shows where the pictures will land before any are asked for', () => {
      const empty = el.querySelector('cp-empty-state.no-pictures')!;
      expect(empty.textContent).toContain('No pictures yet');
      expect(empty.querySelector('cp-illustration')).not.toBeNull();
      expect(el.querySelector('cp-generated')).toBeNull();
    });

    it('marks the set once, round the grid, even when the server named nothing that made them', async () => {
      await arrive([picture(0), picture(1)]);

      const generated = el.querySelector('cp-generated')!;
      expect(generated.querySelector('.sheet')).not.toBeNull();
      expect(generated.querySelector('cp-generated-label')?.textContent).toContain('Made by AI');
      // One mark for the set: the label is the surface's, and no tile carries one of its own.
      expect(generated.querySelectorAll('.sheet cp-generated-label').length).toBe(0);
      expect(el.querySelector('cp-empty-state.no-pictures')).toBeNull();
    });

    it('names what made them when the server says, on the set', async () => {
      host.state.set({ operationId: 'op-1' });
      await settle();
      watches[watches.length - 1].next({
        status: 'found',
        operation: { ...operation([picture(0)]), providerName: 'Venice', modelName: 'flux-dev' },
      });
      await settle();

      expect(el.querySelector('cp-generated cp-generated-label')?.textContent).toContain('Venice, flux-dev');
    });

    it('puts tidying away the rest with the set, as its action', async () => {
      await arrive([picture(0), picture(1)]);

      const tidy = Array.from(el.querySelectorAll('button')).find((b) => b.closest('[cpGeneratedActions]'));
      expect(tidy).toBeTruthy();
    });

    it('draws each picture still coming as a skeleton tile, at the shape of the ones made', async () => {
      await running([picture(0, { width: 1024, height: 768 })], 3);

      const pending = Array.from(el.querySelectorAll<HTMLElement>('.sheet > li.pending'));
      expect(pending.length).toBe(2);
      expect(pending[0].getAttribute('aria-hidden')).toBe('true');
      const skeleton = pending[0].querySelector<HTMLElement>('cp-skeleton')!;
      // The tile's own frame round a picture of the made one's shape, not a card in a tile.
      expect(skeleton.getAttribute('data-shape')).toBe('block');
      expect(skeleton.style.getPropertyValue('--cp-skeleton-picture-aspect')).toBe('1024 / 768');
      // The made one is a real tile, in the same grid.
      expect(el.querySelectorAll('.sheet > li:not(.pending)').length).toBe(1);
    });

    it('draws nothing as on its way once this page has stopped checking, since nothing is being watched for', async () => {
      await running([picture(0)], 3);
      expect(el.querySelectorAll('.sheet > li.pending').length).toBe(2);

      (Array.from(el.querySelectorAll('button')).find((b) => b.textContent?.trim() === 'Stop checking') as HTMLButtonElement).click();
      await settle();

      expect(el.querySelectorAll('.sheet > li.pending').length).toBe(0);
      expect(tiles().length).withContext('the picture already made stays').toBe(1);
    });

    it('draws the whole set square before any picture has been made, and none once the run stops', async () => {
      await running([], 2);

      const pending = Array.from(el.querySelectorAll<HTMLElement>('.sheet > li.pending'));
      expect(pending.length).toBe(2);
      expect(pending[0].querySelector<HTMLElement>('cp-skeleton')!.style.getPropertyValue('--cp-skeleton-picture-aspect')).toBe('1 / 1');
      // No mark round skeletons alone: there is no output yet to say a model made.
      expect(el.querySelector('cp-generated')).toBeNull();
      expect(el.querySelector('[cpGeneratedActions]')).toBeNull();

      watches[watches.length - 1].next({ status: 'found', operation: operation([picture(0), picture(1)]) });
      await settle();
      expect(el.querySelectorAll('.sheet > li.pending').length).toBe(0);
    });
  });

  describe('filing pictures in the library (AF.4.2)', () => {
    const LIBRARY: GeneratedImageRunLibrary = {
      defaultTitle: 'Soda bread hero',
      defaultRecipe: { id: 'r1', title: 'Soda bread' },
      references: [],
    };

    /** Arrive with a run on screen on a surface that files pictures. */
    async function filing(
      images: readonly StagedImage[] = [picture(1), picture(2)],
      library: GeneratedImageRunLibrary = LIBRARY,
    ): Promise<void> {
      host.library.set(library);
      await arrive(images);
    }

    it('offers one control for the decision — saving — and no keeper to mark as well', async () => {
      await filing();

      expect(labelled('button', 'Save to library: picture 1 of 2')).not.toBeNull();
      expect(el.querySelector('input[type="checkbox"]')).toBeNull();
      expect(el.textContent).toContain(ELSEWHERE.chooseHeading);
      expect(el.textContent).toContain('Nothing saved to your library yet.');
    });

    it('keeps the keeper checkbox where there is no library, so the pipeline is untouched', async () => {
      await arrive([picture(1), picture(2)]);

      expect(el.querySelector('input[type="checkbox"]')).not.toBeNull();
      expect(labelled('button', 'Save to library: picture 1 of 2')).toBeNull();
      expect(dialog()).toBeNull();
    });

    it('opens the save form for the picture that was pressed, and for no other', async () => {
      await filing();
      expect(dialog()?.open()).toBeFalse();

      labelled('button', 'Save to library: picture 2 of 2')!.click();
      await settle();

      expect(dialog()?.open()).toBeTrue();
      expect(dialog()?.generatedImageId()).toBe('img-2');
      expect(dialog()?.defaultTitle()).toBe('Soda bread hero');
      expect(dialog()?.defaultRecipe()).toEqual({ id: 'r1', title: 'Soda bread' });
    });

    it('offers no prompt with the save, because a run keeps request ids and not a prompt’s lineage', async () => {
      await filing();

      labelled('button', 'Save to library: picture 1 of 2')!.click();
      await settle();

      expect(dialog()?.prompt()).toBeNull();
      // Alt text is the creator's, and nothing here has read these pixels (.claude/rules/media.md).
      expect(dialog()?.altTextSuggestion()).toBeNull();
    });

    it('shows a saved picture as saved, with the way to its asset, and hands the asset to its caller', async () => {
      await filing();

      labelled('button', 'Save to library: picture 1 of 2')!.click();
      await settle();

      dialog()!.saved.emit(createdAsset());
      await settle();

      expect(host.saves).toEqual([createdAsset()]);
      expect(tiles()[0].textContent).toContain('In your library as “Soda bread hero”.');
      expect(
        labelled('a', 'Open it: picture 1 of 2 in your library')?.getAttribute('href'),
      ).toBe('/cozy-fall/dam/a1');
      expect(host.announcements).toContain('Saved to your library as “Soda bread hero”.');
      expect(el.textContent).toContain('One picture is in your library.');
    });

    it('will not let a saved picture be declined, and offers it no second save', async () => {
      await filing();

      labelled('button', 'Save to library: picture 1 of 2')!.click();
      await settle();
      dialog()!.saved.emit(createdAsset());
      await settle();

      expect(labelled('button', 'Decline picture 1 of 2')).toBeNull();
      expect(labelled('button', 'Save to library: picture 1 of 2')).toBeNull();
      // The other picture is untouched: one save is about one picture.
      expect(labelled('button', 'Decline picture 2 of 2')).not.toBeNull();
      expect(labelled('button', 'Save to library: picture 2 of 2')).not.toBeNull();
    });

    it('says a save is running, then says why it did not land and offers another go', async () => {
      await filing();

      labelled('button', 'Save to library: picture 1 of 2')!.click();
      await settle();

      dialog()!.progress.emit({ state: 'saving' });
      await settle();
      expect(tiles()[0].textContent).toContain('Saving picture 1 of 2 to your library…');
      expect(labelled('button', 'Decline picture 1 of 2')).toBeNull();

      dialog()!.progress.emit({ state: 'failed', problem: 'Nothing was saved.' });
      await settle();
      expect(tiles()[0].querySelector('[role="alert"]')?.textContent).toContain('Nothing was saved.');

      // Nothing was lost: the picture is exactly where it was, and the form opens again for it.
      labelled('button', 'Try again: save picture 1 of 2')!.click();
      await settle();
      expect(dialog()?.open()).toBeTrue();
      expect(dialog()?.generatedImageId()).toBe('img-1');
    });

    it('reads a picture the server says is in the library as saved, whoever saved it', async () => {
      await filing([picture(1, { status: 'Kept' }), picture(2)]);

      expect(tiles()[0].textContent).toContain('In your library.');
      expect(tiles()[0].textContent).toContain('Filed in your library');
      expect(labelled('button', 'Save to library: picture 1 of 2')).toBeNull();
    });

    it('finds the asset of a picture saved before this visit, through the work’s own references', async () => {
      details['a9'] = { status: 'found', asset: assetDetail('a9', 'img-1', 'Last week’s hero') };

      await filing([picture(1, { status: 'Kept' }), picture(2)], {
        ...LIBRARY,
        references: [damReference('ref-1', 'a9', 1)],
      });

      expect(detailReads).toEqual(['cozy-fall|a9']);
      expect(tiles()[0].textContent).toContain('In your library as “Last week’s hero”.');
      expect(labelled('a', 'Open it: picture 1 of 2 in your library')?.getAttribute('href')).toBe('/cozy-fall/dam/a9');
    });

    it('still says a picture is saved when its asset cannot be found, and offers the library instead', async () => {
      await filing([picture(1, { status: 'Kept' })], {
        ...LIBRARY,
        references: [damReference('ref-1', 'gone', 1)],
      });

      expect(tiles()[0].textContent).toContain('In your library.');
      expect(tiles()[0].textContent).not.toContain('In your library as');
      expect(tiles()[0].querySelector('a[href="/cozy-fall/dam"]')).not.toBeNull();
    });

    it('asks for nothing when there is no saved picture to account for', async () => {
      await filing([picture(1), picture(2)], { ...LIBRARY, references: [damReference('ref-1', 'a9', 1)] });

      expect(detailReads).toEqual([]);
    });

    it('stops reading as soon as every saved picture names its asset', async () => {
      details['a9'] = { status: 'found', asset: assetDetail('a9', 'img-1', 'The hero') };

      await filing([picture(1, { status: 'Kept' })], {
        ...LIBRARY,
        references: [damReference('ref-1', 'a9', 1), damReference('ref-2', 'a10', 2)],
      });

      expect(detailReads).toEqual(['cozy-fall|a9']);
    });

    it('leaves a saved picture out of the tidy-up, and out of when the pictures go', async () => {
      await filing();
      const deadline = el.textContent ?? '';
      expect(deadline).toContain('stop being available after');
      expect(deadline).toContain(ELSEWHERE.expiryRemedy);

      labelled('button', 'Save to library: picture 1 of 2')!.click();
      await settle();
      dialog()!.saved.emit(createdAsset());
      await settle();

      // One left to tidy, and it is the one that was not saved.
      expect(
        Array.from(el.querySelectorAll('button')).some(
          (button) => (button.textContent ?? '').trim() === "Tidy away the ones I'm not saving",
        ),
      ).toBeTrue();

      labelled('button', 'Save to library: picture 2 of 2')!.click();
      await settle();
      dialog()!.saved.emit(createdAsset({ id: 'a2', title: 'Second', sourceGeneratedImageId: 'img-2' }));
      await settle();

      // Everything is permanent now, so there is no deadline left to state and nothing to tidy away.
      expect(el.textContent).not.toContain('stop being available after');
      expect(el.textContent).toContain('Nothing left to tidy away.');
    });

    it('writes no keeper into the draft, because saving is the whole decision here', async () => {
      await filing();

      labelled('button', 'Save to library: picture 1 of 2')!.click();
      await settle();
      dialog()!.saved.emit(createdAsset());
      await settle();

      // Nothing is marked, and nothing is said about a mark: saving is the whole decision on this surface.
      expect(host.keeps).toEqual([]);
    });

    it('lets go of a save when the picture turns out not to be there', async () => {
      await filing();

      labelled('button', 'Save to library: picture 1 of 2')!.click();
      await settle();
      dialog()!.progress.emit({ state: 'failed', problem: 'Nothing was saved.' });
      await settle();

      dialog()!.imageGone.emit();
      await settle();

      expect(tiles()[0].querySelector('[role="alert"]')).toBeNull();
    });

    it('forgets what it knows about a run when the creator makes a new set of pictures', async () => {
      await filing();

      labelled('button', 'Save to library: picture 1 of 2')!.click();
      await settle();
      dialog()!.saved.emit(createdAsset());
      await settle();
      expect(el.textContent).toContain('One picture is in your library.');

      // Another run's pictures have other ids, so a save recorded against this one says nothing about them.
      host.state.set({ operationId: 'op-2' });
      await settle();
      watches[watches.length - 1].next({ status: 'found', operation: { ...operation([picture(1)]), id: 'op-2' } });
      await settle();

      expect(el.textContent).toContain('Nothing saved to your library yet.');
      expect(labelled('button', 'Save to library: picture 1 of 1')).not.toBeNull();
    });
  });

  // ---- the price, before the pictures are asked for (AP.2.6) ---------------------------------------------

  describe('the quote', () => {
    it('says what the pictures will cost and what that leaves, in the server’s figures', async () => {
      await priced();

      expect(quoteAsks).toEqual([2]);
      expect(quoteLine()).toBe('2 pictures · 100 AI credits. 900 AI credits left after this.');
      expect(makeButton().disabled).toBeFalse();
    });

    it('warns when going ahead leaves the allowance nearly spent, and still lets it go ahead', async () => {
      await answerWith((variants) => quote(variants, { remaining: 250, remainingAfter: 150, leavesNearlySpent: true }));

      const warning = el.querySelector('#cp-studio-images-quote-warning');
      expect(warning?.getAttribute('role')).toBe('status');
      expect(warning?.textContent).toContain('That leaves your AI allowance nearly spent until it comes back on');
      expect(makeButton().disabled).toBeFalse();
    });

    it('stops a request the allowance cannot cover, says how many would fit, and keeps what was filled in', async () => {
      await answerWith((variants) =>
        quote(variants, { outcome: 'Exhausted', remaining: 60, remainingAfter: null, affordableVariants: 1 }),
      );

      const refusal = el.querySelector('#cp-studio-images-quote-refusal');
      expect(refusal?.getAttribute('role')).toBe('alert');
      expect(refusal?.textContent).toContain(
        '2 pictures need 100 AI credits, and you have 60 AI credits left — enough for one picture. Change that in the brief.',
      );
      expect(refusal?.textContent).toContain('Your allowance comes back on');

      // Refused here, with its reason attached to the button, and the creator's prompt still on screen.
      expect(makeButton().disabled).toBeTrue();
      expect(makeButton().getAttribute('aria-describedby')).toBe('cp-studio-images-quote-refusal');
      expect(el.querySelector('blockquote')?.textContent).toContain('A tight crop.');

      makeButton().click();
      await settle();
      expect(requests.length).toBe(0);

      // Asking for fewer asks the price again, and the button comes back as soon as the request fits.
      quoteAnswer = (variants) => quote(variants, { remaining: 60, remainingAfter: 10 });
      host.variantCount.set(1);
      await priced();

      expect(quoteAsks.at(-1)).toBe(1);
      expect(makeButton().disabled).toBeFalse();
      expect(makeButton().getAttribute('aria-describedby')).toBeNull();
      // The region stays, quiet and empty, so a later refusal is still announced.
      expect(el.querySelector('#cp-studio-images-quote-refusal')?.textContent?.trim()).toBe('');

      // Operable from the keyboard once it is back: a native button that takes focus.
      makeButton().focus();
      expect(document.activeElement).toBe(makeButton());
    });

    it('stops a request from an account whose AI help is switched off, and says so', async () => {
      await answerWith((variants) =>
        quote(variants, { outcome: 'Suspended', perPicture: null, total: null, allowance: null, remaining: null, remainingAfter: null, affordableVariants: 0, resetsAt: null, timeZoneId: null }),
      );

      expect(el.querySelector('#cp-studio-images-quote-refusal')?.textContent).toContain('AI assistance is switched off');
      expect(makeButton().disabled).toBeTrue();
    });

    it('stops nothing when the price could not be worked out — the request decides', async () => {
      await answerWith(() => null);

      expect(quoteLine()).toBe('We couldn’t work out what this costs just now.');
      expect(makeButton().disabled).toBeFalse();
    });

    it('says nothing about a price on a host with no image provider, and stops nothing', async () => {
      await answerWith((variants) =>
        quote(variants, { outcome: 'Unavailable', unit: null, perPicture: null, total: null, allowance: null, remaining: null, remainingAfter: null, affordableVariants: 0, resetsAt: null, timeZoneId: null }),
      );

      expect(quoteLine()).toBe('');
      expect(makeButton().disabled).toBeFalse();
    });

    it('says nothing while asking, so a change of count is announced once, with its price', async () => {
      host.variantCount.set(3);
      fixture.detectChanges();

      expect(el.querySelector('[id$="-quote"] [role="status"]')?.textContent?.trim()).toBe('');
      expect(quoteLine()).toContain('Working out what this costs…');

      await priced();
      expect(el.querySelector('[id$="-quote"] [role="status"]')?.textContent?.trim()).toBe(
        '3 pictures · 150 AI credits. 850 AI credits left after this.',
      );
    });

    it('keeps its refusal region in the page with no prompt, so the button never points at nothing', async () => {
      host.promptText.set('');
      await answerWith((variants) =>
        quote(variants, { outcome: 'Exhausted', remaining: 0, remainingAfter: null, affordableVariants: 0 }),
      );

      const described = makeButton().getAttribute('aria-describedby');
      expect(described).toBe('cp-studio-images-quote-refusal');
      expect(el.querySelector(`#${described}`)).not.toBeNull();
    });

    it('asks once for a run of quick changes to the count, and shows only the last answer', async () => {
      await priced();

      host.variantCount.set(3);
      fixture.detectChanges();
      host.variantCount.set(4);
      await priced();

      expect(quoteAsks).toEqual([2, 4]);
      expect(quoteLine()).toContain('4 pictures · 200 AI credits.');
    });

    it('asks the price again once a request is accepted, because what is left has moved', async () => {
      requestOutcome = { status: 'accepted', operation: { id: 'op-9', status: 'Requested', variantCount: 2, stagedCount: 0 } } as GeneratedImageRequestOutcome;
      await priced();

      makeButton().click();
      await priced();

      expect(requests.length).toBe(1);
      expect(quoteAsks).toEqual([2, 2]);
    });
  });
});
