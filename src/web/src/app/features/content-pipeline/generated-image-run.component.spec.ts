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
  return Array.from(el.querySelectorAll('.sheet > li'));
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

    expect(requests).toEqual([{ promptText: 'A tight crop.', avoidText: 'clutter, harsh light', variantCount: 4 }]);
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

      expect(labelled('button', 'Save picture 1 of 2 to your library')).not.toBeNull();
      expect(el.querySelector('input[type="checkbox"]')).toBeNull();
      expect(el.textContent).toContain(ELSEWHERE.chooseHeading);
      expect(el.textContent).toContain('Nothing saved to your library yet.');
    });

    it('keeps the keeper checkbox where there is no library, so the pipeline is untouched', async () => {
      await arrive([picture(1), picture(2)]);

      expect(el.querySelector('input[type="checkbox"]')).not.toBeNull();
      expect(labelled('button', 'Save picture 1 of 2 to your library')).toBeNull();
      expect(dialog()).toBeNull();
    });

    it('opens the save form for the picture that was pressed, and for no other', async () => {
      await filing();
      expect(dialog()?.open()).toBeFalse();

      labelled('button', 'Save picture 2 of 2 to your library')!.click();
      await settle();

      expect(dialog()?.open()).toBeTrue();
      expect(dialog()?.generatedImageId()).toBe('img-2');
      expect(dialog()?.defaultTitle()).toBe('Soda bread hero');
      expect(dialog()?.defaultRecipe()).toEqual({ id: 'r1', title: 'Soda bread' });
    });

    it('offers no prompt with the save, because a run keeps request ids and not a prompt’s lineage', async () => {
      await filing();

      labelled('button', 'Save picture 1 of 2 to your library')!.click();
      await settle();

      expect(dialog()?.prompt()).toBeNull();
      // Alt text is the creator's, and nothing here has read these pixels (.claude/rules/media.md).
      expect(dialog()?.altTextSuggestion()).toBeNull();
    });

    it('shows a saved picture as saved, with the way to its asset, and hands the asset to its caller', async () => {
      await filing();

      labelled('button', 'Save picture 1 of 2 to your library')!.click();
      await settle();

      dialog()!.saved.emit(createdAsset());
      await settle();

      expect(host.saves).toEqual([createdAsset()]);
      expect(tiles()[0].textContent).toContain('In your library as “Soda bread hero”.');
      expect(
        labelled('a', 'Open picture 1 of 2 in your library')?.getAttribute('href'),
      ).toBe('/cozy-fall/dam/a1');
      expect(host.announcements).toContain('Saved to your library as “Soda bread hero”.');
      expect(el.textContent).toContain('One picture is in your library.');
    });

    it('will not let a saved picture be declined, and offers it no second save', async () => {
      await filing();

      labelled('button', 'Save picture 1 of 2 to your library')!.click();
      await settle();
      dialog()!.saved.emit(createdAsset());
      await settle();

      expect(labelled('button', 'Decline picture 1 of 2')).toBeNull();
      expect(labelled('button', 'Save picture 1 of 2 to your library')).toBeNull();
      // The other picture is untouched: one save is about one picture.
      expect(labelled('button', 'Decline picture 2 of 2')).not.toBeNull();
      expect(labelled('button', 'Save picture 2 of 2 to your library')).not.toBeNull();
    });

    it('says a save is running, then says why it did not land and offers another go', async () => {
      await filing();

      labelled('button', 'Save picture 1 of 2 to your library')!.click();
      await settle();

      dialog()!.progress.emit({ state: 'saving' });
      await settle();
      expect(tiles()[0].textContent).toContain('Saving picture 1 of 2 to your library…');
      expect(labelled('button', 'Decline picture 1 of 2')).toBeNull();

      dialog()!.progress.emit({ state: 'failed', problem: 'Nothing was saved.' });
      await settle();
      expect(tiles()[0].querySelector('[role="alert"]')?.textContent).toContain('Nothing was saved.');

      // Nothing was lost: the picture is exactly where it was, and the form opens again for it.
      labelled('button', 'Try saving picture 1 of 2 again')!.click();
      await settle();
      expect(dialog()?.open()).toBeTrue();
      expect(dialog()?.generatedImageId()).toBe('img-1');
    });

    it('reads a picture the server says is in the library as saved, whoever saved it', async () => {
      await filing([picture(1, { status: 'Kept' }), picture(2)]);

      expect(tiles()[0].textContent).toContain('In your library.');
      expect(tiles()[0].textContent).toContain('Filed in your library');
      expect(labelled('button', 'Save picture 1 of 2 to your library')).toBeNull();
    });

    it('finds the asset of a picture saved before this visit, through the work’s own references', async () => {
      details['a9'] = { status: 'found', asset: assetDetail('a9', 'img-1', 'Last week’s hero') };

      await filing([picture(1, { status: 'Kept' }), picture(2)], {
        ...LIBRARY,
        references: [damReference('ref-1', 'a9', 1)],
      });

      expect(detailReads).toEqual(['cozy-fall|a9']);
      expect(tiles()[0].textContent).toContain('In your library as “Last week’s hero”.');
      expect(labelled('a', 'Open picture 1 of 2 in your library')?.getAttribute('href')).toBe('/cozy-fall/dam/a9');
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

      labelled('button', 'Save picture 1 of 2 to your library')!.click();
      await settle();
      dialog()!.saved.emit(createdAsset());
      await settle();

      // One left to tidy, and it is the one that was not saved.
      expect(
        Array.from(el.querySelectorAll('button')).some(
          (button) => (button.textContent ?? '').trim() === "Tidy away the ones I'm not saving",
        ),
      ).toBeTrue();

      labelled('button', 'Save picture 2 of 2 to your library')!.click();
      await settle();
      dialog()!.saved.emit(createdAsset({ id: 'a2', title: 'Second', sourceGeneratedImageId: 'img-2' }));
      await settle();

      // Everything is permanent now, so there is no deadline left to state and nothing to tidy away.
      expect(el.textContent).not.toContain('stop being available after');
      expect(el.textContent).toContain('Nothing left to tidy away.');
    });

    it('writes no keeper into the draft, because saving is the whole decision here', async () => {
      await filing();

      labelled('button', 'Save picture 1 of 2 to your library')!.click();
      await settle();
      dialog()!.saved.emit(createdAsset());
      await settle();

      // Nothing is marked, and nothing is said about a mark: saving is the whole decision on this surface.
      expect(host.keeps).toEqual([]);
    });

    it('lets go of a save when the picture turns out not to be there', async () => {
      await filing();

      labelled('button', 'Save picture 1 of 2 to your library')!.click();
      await settle();
      dialog()!.progress.emit({ state: 'failed', problem: 'Nothing was saved.' });
      await settle();

      dialog()!.imageGone.emit();
      await settle();

      expect(tiles()[0].querySelector('[role="alert"]')).toBeNull();
    });

    it('forgets what it knows about a run when the creator makes a new set of pictures', async () => {
      await filing();

      labelled('button', 'Save picture 1 of 2 to your library')!.click();
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
      expect(labelled('button', 'Save picture 1 of 1 to your library')).not.toBeNull();
    });
  });
});
