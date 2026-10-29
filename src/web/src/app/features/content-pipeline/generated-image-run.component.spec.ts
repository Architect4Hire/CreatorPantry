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
  GeneratedImageQualityClass,
  GeneratedImageQualityClasses,
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
    [workspaceSlug]="slug()"
    [promptText]="promptText()"
    [avoidText]="avoidText()"
    [variantCount]="variantCount()"
    [subject]="subject()"
    [state]="state()"
    [wording]="wording()"
    [sectionIdPrefix]="prefix()"
    [library]="library()"
    [keepers]="keepers()"
    [showMaker]="showMaker()"
    (changed)="changes.push($event); state.set($event)"
    (keepToggled)="keeps.push($event)"
    (announced)="announcements.push($event)"
    (saved)="saves.push($event)"
  />`,
})
class HostComponent {
  readonly slug = signal('cozy-fall');
  readonly promptText = signal('A tight crop.');
  readonly avoidText = signal<string | null>(null);
  readonly variantCount = signal(2);
  readonly subject = signal<GeneratedImageSubject | null>(null);
  readonly state = signal<ContentPipelineImagesState>({ operationId: null });
  readonly wording = signal<GeneratedImageRunWording>(ELSEWHERE);
  readonly prefix = signal('cp-studio-images');
  readonly library = signal<GeneratedImageRunLibrary | null>(null);
  readonly keepers = signal<readonly string[]>([]);
  readonly showMaker = signal(true);

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
let quoteClasses: (GeneratedImageQualityClass | null)[];
let quoteAnswer: (variants: number, qualityClass: GeneratedImageQualityClass | null) => GeneratedImageQuote | null;
let classesAnswer: () => Promise<GeneratedImageQualityClasses | null>;
let classesReads: number;

/**
 * The price list a host with three classes would send: Standard the default, cheapest first (AP.3.1).
 *
 * A third element in a row locks that class, as a plan that does not allow it does (AP.4.3): the plan's name, or
 * `null` for a class no plan allows. A row of two is allowed.
 */
function classes(
  rows: readonly (readonly [GeneratedImageQualityClass, number, (string | null)?])[] = [
    ['Draft', 25],
    ['Standard', 50],
    ['High', 87.5],
  ],
  defaultClass: GeneratedImageQualityClass | null = 'Standard',
): GeneratedImageQualityClasses {
  return {
    defaultClass,
    unit: 'Credits',
    classes: rows.map((row) => ({
      qualityClass: row[0],
      perPicture: row[1],
      isDefault: row[0] === defaultClass,
      allowed: row.length < 3,
      requiresPlan: row.length < 3 || row[2] == null ? null : row[2]!.toLowerCase(),
      requiresPlanName: row.length < 3 ? null : (row[2] ?? null),
    })),
  };
}

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
    packRemaining: 0,
    packRemainingAfter: 0,
    affordableVariants: variants,
    leavesNearlySpent: false,
    resetsAt: '2026-11-01T00:00:00+00:00',
    timeZoneId: 'Etc/UTC',
    pictureCap: null,
    picturesLeft: null,
    pictureCapResetsAt: null,
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

/** One quality's radio, by the class it stands for. */
function qualityRadio(qualityClass: GeneratedImageQualityClass): HTMLInputElement {
  return el.querySelector(`#cp-studio-images-quality-${qualityClass}`) as HTMLInputElement;
}

function qualityGroup(): HTMLElement | null {
  return el.querySelector('[role="radiogroup"][aria-label="Picture quality"]');
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
    quoteClasses = [];
    quoteAnswer = (variants) => quote(variants);
    classesAnswer = () => Promise.resolve(classes());
    classesReads = 0;
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
            quote: (_slug: string, variants: number, qualityClass: GeneratedImageQualityClass | null = null) => {
              quoteAsks.push(variants);
              quoteClasses.push(qualityClass);
              return Promise.resolve(quoteAnswer(variants, qualityClass));
            },
            qualityClasses: () => {
              classesReads += 1;
              return classesAnswer();
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
      {
        promptText: 'A tight crop.',
        avoidText: 'clutter, harsh light',
        variantCount: 4,
        subject: null,
        qualityClass: 'Standard',
      },
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

    it('takes a new key when only the quality changed, since the key names the class too (AP.3.1)', async () => {
      await ask();

      qualityRadio('High').click();
      await settle();
      await ask();

      expect(requests.map((request) => request.qualityClass)).toEqual(['Standard', 'High']);
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

    it('says the allowance and the pack balance apart when the creator has credit packs (AP.5.2)', async () => {
      // 30 left in the allowance and 400 in packs: the request takes the 30 first, then 70 from packs. The figures
      // are the server's; the sentence names each source and does no arithmetic.
      await answerWith((variants) =>
        quote(variants, { remaining: 30, remainingAfter: 0, packRemaining: 400, packRemainingAfter: 330 }),
      );

      expect(quoteLine()).toBe(
        '2 pictures · 100 AI credits. 0 AI credits of your allowance and 330 AI credits in credit packs left after this.',
      );
      expect(makeButton().disabled).toBeFalse();
    });

    it('names the pack balance in a refusal, beside the allowance that resets (AP.5.2)', async () => {
      await answerWith((variants) =>
        quote(variants, {
          outcome: 'Exhausted',
          remaining: 20,
          remainingAfter: null,
          packRemaining: 40,
          packRemainingAfter: null,
          affordableVariants: 1,
        }),
      );

      expect(el.querySelector('#cp-studio-images-quote-refusal')?.textContent).toContain(
        '2 pictures need 100 AI credits, and you have 20 AI credits of your allowance and 40 AI credits in credit packs left — enough for one picture.',
      );
      expect(makeButton().disabled).toBeTrue();
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

    it('stops a request from an account whose free trial has ended, and says what to do', async () => {
      await answerWith((variants) =>
        quote(variants, { outcome: 'TrialEnded', perPicture: null, total: null, allowance: null, remaining: null, remainingAfter: null, affordableVariants: 0, resetsAt: null, timeZoneId: null }),
      );

      expect(el.querySelector('#cp-studio-images-quote-refusal')?.textContent).toContain('Your free trial has ended');
      expect(el.querySelector('#cp-studio-images-quote-refusal')?.textContent).toContain('Choose a plan');
      expect(makeButton().disabled).toBeTrue();
    });

    // AP.7.2: the quote judges the picture caps admission does, so the run stops before asking — in the server's
    // figures, and offering the count that would fit.
    it('stops a request that would pass the trial’s pictures, and says how many are left', async () => {
      await answerWith((variants) =>
        quote(variants, { outcome: 'TrialImageCap', pictureCap: 10, picturesLeft: 2, affordableVariants: 2, remainingAfter: null }),
      );

      const refusal = el.querySelector('#cp-studio-images-quote-refusal')?.textContent ?? '';
      expect(refusal).toContain('Your free trial includes 10 pictures and has 2 pictures left');
      expect(makeButton().disabled).toBeTrue();
    });

    it('stops a request that would pass the day’s pictures, and says when more come back', async () => {
      await answerWith((variants) =>
        quote(variants, {
          outcome: 'DailyImageCap',
          pictureCap: 5,
          picturesLeft: 0,
          affordableVariants: 0,
          remainingAfter: null,
          pictureCapResetsAt: '2026-10-28T00:00:00+00:00',
        }),
      );

      const refusal = el.querySelector('#cp-studio-images-quote-refusal')?.textContent ?? '';
      expect(refusal).toContain('5 pictures a day');
      expect(refusal).toContain('0 pictures left today');
      expect(refusal).toContain('(UTC)');
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

    it('is asked where the maker is left off the provenance line, because that hides a name and not a price', async () => {
      await rebuild((next) => next.showMaker.set(false));
      await priced();

      expect(quoteAsks.length).toBeGreaterThan(0);
      expect(quoteLine()).toBe('2 pictures · 100 AI credits. 900 AI credits left after this.');
      expect(qualityGroup()).not.toBeNull();
    });
  });

  // ---- how good each picture is (AP.3.1A) ---------------------------------------------------------------

  describe('the quality', () => {
    function countLine(): HTMLElement | undefined {
      return Array.from(el.querySelectorAll<HTMLElement>('p')).find((p) => (p.textContent ?? '').includes('Asking for'));
    }

    it('offers each class with its own price, the default picked, before the count', async () => {
      await priced();

      const group = qualityGroup();
      expect(group).not.toBeNull();

      const tiles = Array.from(group!.querySelectorAll('.tile')).map((tile) => [
        tile.querySelector('.label')?.textContent?.trim(),
        tile.querySelector('.hint')?.textContent?.trim(),
      ]);
      expect(tiles).toEqual([
        ['Draft', '25 AI credits a picture. Quick, for trying an idea.'],
        ['Standard', '50 AI credits a picture. Good for most posts.'],
        ['High', '87.5 AI credits a picture. Finer detail.'],
      ]);
      expect(qualityRadio('Standard').checked).toBeTrue();

      // Before the count it prices, so "2 pictures · 100 AI credits" reads as following the choice.
      expect(group!.compareDocumentPosition(countLine()!) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();

      // The default is what the first price was asked for.
      expect(quoteClasses).toEqual(['Standard']);
    });

    it('asks the price again for the class chosen, and leaves the prompt and the count as they were', async () => {
      quoteAnswer = (variants, qualityClass) =>
        qualityClass === 'High'
          ? quote(variants, { perPicture: 87.5, total: variants * 87.5, remainingAfter: 1000 - variants * 87.5 })
          : quote(variants);
      await priced();

      qualityRadio('High').click();
      await priced();

      expect(quoteClasses.at(-1)).toBe('High');
      expect(quoteLine()).toBe('2 pictures · 175 AI credits. 825 AI credits left after this.');

      expect(el.querySelector('blockquote')?.textContent).toContain('A tight crop.');
      expect(countLine()?.textContent).toContain('Asking for 2 pictures.');
      expect(host.changes).toEqual([]);
    });

    it('asks once for a run of quick changes of class, for the last one', async () => {
      await priced();
      const before = quoteAsks.length;

      qualityRadio('High').click();
      fixture.detectChanges();
      qualityRadio('Draft').click();
      await priced();

      expect(quoteAsks.length).toBe(before + 1);
      expect(quoteClasses.at(-1)).toBe('Draft');
    });

    it('sends the chosen class on the request', async () => {
      await priced();

      qualityRadio('Draft').click();
      await settle();
      makeButton().click();
      await settle();

      expect(requests.length).toBe(1);
      expect(requests[0].qualityClass).toBe('Draft');
      expect(requests[0].promptText).toBe('A tight crop.');
      expect(requests[0].variantCount).toBe(2);
    });

    it('is operated by the keyboard as one radio group: real radios, one name, focus kept on the one picked', async () => {
      await priced();

      const radios = Array.from(qualityGroup()!.querySelectorAll<HTMLInputElement>('input'));
      expect(radios.every((radio) => radio.type === 'radio')).toBeTrue();
      expect(new Set(radios.map((radio) => radio.name)).size).toBe(1);
      expect(radios.filter((radio) => radio.checked).map((radio) => radio.value)).toEqual(['Standard']);
      expect(radios.some((radio) => radio.disabled)).toBeFalse();

      // An arrow key moves focus and checks the next radio, which the browser does and a test cannot press; what
      // the page owes is that a check from there is taken, and that the group is not redrawn under the focus.
      const high = qualityRadio('High');
      high.focus();
      high.click();
      await priced();

      expect(qualityRadio('High').checked).toBeTrue();
      expect(document.activeElement).toBe(qualityRadio('High'));
      expect(quoteClasses.at(-1)).toBe('High');
    });

    it('names what fits at an unaffordable class and leaves the choice open', async () => {
      quoteAnswer = (variants, qualityClass) =>
        qualityClass === 'High'
          ? quote(variants, { outcome: 'Exhausted', perPicture: 87.5, total: 175, remaining: 100, remainingAfter: null, affordableVariants: 1 })
          : quote(variants, { remaining: 100, remainingAfter: 0 });
      await priced();

      qualityRadio('High').click();
      await priced();

      const refusal = el.querySelector('#cp-studio-images-quote-refusal')?.textContent ?? '';
      expect(refusal).toContain(
        'At High quality, 2 pictures need 175 AI credits, and you have 100 AI credits left — enough for one picture.',
      );
      expect(refusal).toContain('You can also choose a lower quality.');
      expect(makeButton().disabled).toBeTrue();

      // Every class can still be chosen, and the prompt is still there.
      expect(Array.from(qualityGroup()!.querySelectorAll('input')).some((radio) => radio.disabled)).toBeFalse();
      expect(el.querySelector('blockquote')?.textContent).toContain('A tight crop.');

      qualityRadio('Standard').click();
      await priced();

      expect(makeButton().disabled).toBeFalse();
      expect(el.querySelector('#cp-studio-images-quote-refusal')?.textContent?.trim()).toBe('');
    });

    it('suggests no lower quality when the lowest is the one that does not fit', async () => {
      quoteAnswer = (variants) =>
        quote(variants, { outcome: 'Exhausted', perPicture: 25, total: 50, remaining: 10, remainingAfter: null, affordableVariants: 0 });
      await priced();

      qualityRadio('Draft').click();
      await priced();

      const refusal = el.querySelector('#cp-studio-images-quote-refusal')?.textContent ?? '';
      expect(refusal).toContain('At Draft quality, 2 pictures need 50 AI credits, and you have 10 AI credits left.');
      expect(refusal).not.toContain('lower quality');
    });

    it('starts another workspace from its own default, not from the pick made in the last one', async () => {
      await priced();
      qualityRadio('High').click();
      await priced();

      host.slug.set('other-kitchen');
      await priced();

      expect(qualityRadio('Standard').checked).toBeTrue();
      expect(quoteClasses.at(-1)).toBe('Standard');
    });

    it('treats a price list it cannot word as one it could not read, rather than showing classes with no price', async () => {
      await rebuild(() => (classesAnswer = () => Promise.resolve({ ...classes(), unit: 'Tokens' })));
      await priced();

      expect(qualityGroup()).toBeNull();
      expect(el.querySelector('#cp-studio-images-quality-problem')?.textContent).toContain(
        'couldn’t load the picture qualities',
      );
      expect(quoteClasses.at(-1)).toBeNull();
    });

    it('holds the place while the list is loading, blocks nothing, and holds the price until it knows the class', async () => {
      await rebuild(() => (classesAnswer = () => new Promise(() => undefined)));
      await priced();

      const loading = el.querySelector('.quality[aria-busy="true"]');
      expect(loading).not.toBeNull();
      expect(loading?.querySelector('cp-skeleton')).not.toBeNull();

      // Said once from a region that was in the page before it had words, outside the busy body.
      const said = Array.from(el.querySelectorAll('.cp-sr-only[role="status"]')).map((node) => node.textContent?.trim());
      expect(said).toContain('Finding the picture qualities…');
      expect(qualityGroup()).toBeNull();

      // No price asked yet, so none that changes a moment later; "Working out…" says so, and stops nothing.
      expect(quoteClasses).toEqual([]);
      expect(quoteLine()).toContain('Working out what this costs…');
      expect(makeButton().disabled).toBeFalse();

      makeButton().click();
      await settle();
      expect(requests[0].qualityClass).toBeNull();
    });

    it('says the list could not be read, makes pictures at the usual quality, and loads it again on request', async () => {
      await rebuild(() => (classesAnswer = () => Promise.resolve(null)));
      await priced();

      // The region is the one that was there, quiet, before anything failed — so the failure is announced.
      const notice = el.querySelector('#cp-studio-images-quality-problem');
      expect(notice?.getAttribute('role')).toBe('status');
      expect(notice?.textContent).toContain('couldn’t load the picture qualities');
      expect(notice?.textContent).toContain('so pictures are made at the usual quality');
      expect(qualityGroup()).toBeNull();
      expect(makeButton().disabled).toBeFalse();
      expect(quoteClasses.at(-1)).toBeNull();

      classesAnswer = () => Promise.resolve(classes());
      const again = Array.from(notice!.querySelectorAll('button')).find(
        (button) => (button.textContent ?? '').trim() === 'Load the qualities again',
      )!;
      again.click();
      await priced();

      expect(classesReads).toBe(2);
      expect(qualityRadio('Standard').checked).toBeTrue();
      expect(quoteClasses.at(-1)).toBe('Standard');

      // The button went with the notice; focus is on the quality picked rather than lost to the page, and the
      // region stays, quiet, for a later failure.
      expect(document.activeElement).toBe(qualityRadio('Standard'));
      expect(el.querySelector('#cp-studio-images-quality-problem')?.textContent?.trim()).toBe('');
    });

    it('puts focus back on the button when loading again fails again', async () => {
      await rebuild(() => (classesAnswer = () => Promise.resolve(null)));
      await priced();

      const button = () => el.querySelector<HTMLButtonElement>('#cp-studio-images-quality-problem button')!;
      button().click();
      await priced();

      expect(classesReads).toBe(2);
      expect(document.activeElement).toBe(button());
    });

    it('says the one class a host offers rather than offering a choice of one, and sends it', async () => {
      await rebuild(() => (classesAnswer = () => Promise.resolve(classes([['High', 87.5]], 'High'))));
      await priced();

      expect(qualityGroup()).toBeNull();
      expect(el.textContent).toContain('Pictures are made at High quality — 87.5 AI credits a picture.');

      makeButton().click();
      await settle();
      expect(requests[0].qualityClass).toBe('High');
    });

    it('offers nothing on a host with no classes, and names none on the request', async () => {
      await rebuild(() => (classesAnswer = () => Promise.resolve(classes([], null))));
      await priced();

      expect(qualityGroup()).toBeNull();
      expect(el.textContent).not.toContain('Picture quality');
      expect(el.textContent).not.toContain('Pictures are made at');

      makeButton().click();
      await settle();
      expect(requests[0].qualityClass).toBeNull();
    });

    // ---- what the plan allows (AP.4.3) -----------------------------------------------------------------

    it('shows a class the plan does not allow as locked, with its price and the plan that would', async () => {
      await rebuild(
        () =>
          (classesAnswer = () =>
            Promise.resolve(classes([['Draft', 25], ['Standard', 50], ['High', 87.5, 'Creator']]))),
      );
      await priced();

      const tiles = Array.from(qualityGroup()!.querySelectorAll('.tile')).map((tile) => [
        tile.querySelector('.label')?.textContent?.trim(),
        tile.querySelector('.hint')?.textContent?.trim(),
        (tile.querySelector('input') as HTMLInputElement).disabled,
      ]);

      expect(tiles).toEqual([
        ['Draft', '25 AI credits a picture. Quick, for trying an idea.', false],
        ['Standard', '50 AI credits a picture. Good for most posts.', false],
        // Still priced, so the plan can be weighed against the figure, and "Locked" is a word as well as a
        // greyed tile.
        ['High', '87.5 AI credits a picture. Locked — the Creator plan includes it.', true],
      ]);

      // The pick is the default, and the price asked for is the default's: a locked class is never quoted.
      expect(qualityRadio('Standard').checked).toBeTrue();
      expect(quoteClasses).toEqual(['Standard']);
    });

    it('says a class no plan allows is not on one yet, rather than leaving "Locked" as a dead end', async () => {
      await rebuild(
        () => (classesAnswer = () => Promise.resolve(classes([['Standard', 50], ['High', 87.5, null]]))),
      );
      await priced();

      const locked = qualityGroup()!.querySelectorAll('.tile')[1];
      expect(locked.querySelector('.hint')?.textContent?.trim()).toBe(
        '87.5 AI credits a picture. Locked — not on any plan yet.',
      );
    });

    /**
     * The reason has to be reachable, not only inside the tile: a disabled radio is out of the tab order and out
     * of the group's arrow-key movement, so a creator moving by keyboard never lands on it. DESIGN-SYSTEM.md's
     * locked-control rule wants the explaining line, and the group points at it.
     */
    it('says what the plan locks in reading order, and describes the group by it', async () => {
      await rebuild(
        () =>
          (classesAnswer = () =>
            Promise.resolve(
              classes([['Draft', 25], ['Standard', 50], ['High', 87.5, 'Creator'], ['High2K', 187.5, null]]),
            )),
      );
      await priced();

      const said = el.querySelector('#cp-studio-images-quality-locked');
      expect(said?.textContent?.trim()).toBe(
        'Not on your plan: High comes with the Creator plan; High, 2K is not on any plan yet.',
      );

      // Said when the group is entered, so it is not visual only.
      expect(qualityGroup()?.getAttribute('aria-describedby')).toBe('cp-studio-images-quality-locked');
    });

    it('says nothing about plans, and describes the group by nothing, when every class is on the plan', async () => {
      await priced();

      expect(el.querySelector('#cp-studio-images-quality-locked')).toBeNull();
      expect(qualityGroup()?.getAttribute('aria-describedby')).toBeNull();
      expect(el.textContent).not.toContain('Not on your plan');
    });

    it('cannot be sent a locked class: the tile is not an answer the keyboard or the pointer can give', async () => {
      await rebuild(
        () =>
          (classesAnswer = () =>
            Promise.resolve(classes([['Draft', 25], ['Standard', 50], ['High', 87.5, 'Creator']]))),
      );
      await priced();

      // A disabled radio is out of the group's arrow-key movement as well as out of the tab order, so the
      // keyboard cannot reach it either; clicking it reports nothing.
      qualityRadio('High').click();
      await priced();

      expect(qualityRadio('High').checked).toBeFalse();
      expect(qualityRadio('Standard').checked).toBeTrue();

      makeButton().click();
      await settle();
      expect(requests.at(-1)!.qualityClass).toBe('Standard');
      expect(quoteClasses).not.toContain('High');
    });

    it('picks nothing when the one class offered is locked, so the request names none', async () => {
      await rebuild(() => (classesAnswer = () => Promise.resolve(classes([['High', 87.5, 'Studio']], null))));
      await priced();

      makeButton().click();
      await settle();
      expect(requests[0].qualityClass).toBeNull();
    });

    it('offers no lower quality in a refusal when every lower one is locked', async () => {
      quoteAnswer = (variants, qualityClass) =>
        qualityClass === 'High'
          ? quote(variants, { outcome: 'Exhausted', perPicture: 87.5, total: variants * 87.5, remaining: 10, affordableVariants: 0 })
          : quote(variants);
      await rebuild(
        () =>
          (classesAnswer = () =>
            Promise.resolve(classes([['Draft', 25, 'Creator'], ['High', 87.5]], 'High'))),
      );
      await priced();

      expect(qualityRadio('High').checked).toBeTrue();
      expect(el.textContent).toContain('At High quality');
      expect(el.textContent).not.toContain('lower quality');
    });
  });
});

/** A fresh run, set up by `configure` before it first renders — for what it reads as it arrives. */
async function rebuild(configure: (next: HostComponent) => void): Promise<void> {
  fixture.destroy();
  quoteAsks = [];
  quoteClasses = [];
  classesReads = 0;

  fixture = TestBed.createComponent(HostComponent);
  host = fixture.componentInstance;
  el = fixture.nativeElement;
  configure(host);
  await settle();
}
