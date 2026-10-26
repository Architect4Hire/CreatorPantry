import { Component, Type, inject, input, output, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';

import { WorkspaceRole } from '../../../models/auth.models';
import { CreativeContext, CreativeContextReference } from '../../../models/creative-context.models';
import { RecipeAssetLink, RecipeDetail } from '../../../models/recipe.models';
import { WorkflowResourcePointer, WorkflowRun, decodeWorkflowRun } from '../../../models/workflow-run.models';
import { wireRun } from '../../../models/workflow-run.testing';
import { BrandProfileService } from '../../../services/brand-profile.service';
import { CreativeContextService } from '../../../services/creative-context.service';
import { DamAssetService } from '../../../services/dam-asset.service';
import { RecipeAssetLinkService } from '../../../services/recipe-asset-link.service';
import { RecipeService } from '../../../services/recipe.service';
import { WorkspaceMembershipService } from '../../../services/workspace-membership.service';
import { WorkflowStepPort } from '../../../shared/workflow-shell/workflow-step-port';
import { ImageStudioComponent } from '../../image-studio/image-studio.component';
import { ImageStudioHost } from '../../image-studio/image-studio-host';
import { RecipeMediaComponent } from '../../recipes/recipe-media.component';
import { ImagesLeadPictureStepComponent } from './images-lead-picture-step.component';
import { ImagesStudioStepComponent } from './images-studio-step.component';

const SLUG = 'sams-kitchen';
const RUN_ID = '0f6f2a54-7c1e-4d4b-9a51-2f1b3c4d5e6f';

/** The studio, stood in for: what the step reads of it, and what it saw of the host it was given. */
@Component({
  selector: 'cp-image-studio',
  template: `<p class="stub-studio">{{ seenPart }}</p>`,
  providers: [{ provide: ImageStudioComponent, useExisting: StubStudioComponent }],
})
class StubStudioComponent {
  readonly host = inject(ImageStudioHost);
  /** Read as the studio reads it: once, when it is made. */
  readonly seenPart = this.host.part;
  readonly seenContextId = this.host.contextId;
  readonly seenSuggestion = this.host.suggestedRecipeId;
  readonly workContext = signal<CreativeContext | null>(null);
  readonly sending = signal(false);
  flushed = 0;
  flushAnswer = true;

  flushWork(): Promise<boolean> {
    this.flushed += 1;
    return Promise.resolve(this.flushAnswer);
  }
}

@Component({ selector: 'cp-recipe-media', template: `<p class="stub-media">media</p>` })
class StubRecipeMediaComponent {
  readonly workspaceSlug = input<string>();
  readonly recipeId = input<string>();
  readonly links = input<readonly RecipeAssetLink[]>();
  readonly steps = input<unknown>();
  readonly concurrencyToken = input<string | null>();
  readonly canChange = input(false);
  readonly isArchived = input(false);
  readonly changed = output<unknown>();
  readonly reloadRequested = output<void>();
}

function reference(overrides: Partial<CreativeContextReference>): CreativeContextReference {
  return {
    id: 'ref-1',
    kind: 'DamAsset',
    purpose: 'Keeper',
    sortOrder: 0,
    recipeId: null,
    recipeVersionId: null,
    conceptRequestId: null,
    conceptId: null,
    mediaAssetId: 'asset-1',
    mediaAssetVersionNumber: 1,
    generatedImageId: null,
    promptRecordId: null,
    addedAt: '2026-10-15T10:00:00Z',
    ...overrides,
  };
}

function context(references: readonly CreativeContextReference[]): CreativeContext {
  return {
    id: 'ctx-1',
    workingTitle: null,
    pictureBrief: null,
    briefSource: null,
    workingBrief: null,
    channelKeys: [],
    day: null,
    weeklyThemeKey: null,
    references,
    createdAt: '2026-10-15T09:00:00Z',
    updatedAt: '2026-10-15T10:00:00Z',
    archivedAt: null,
    concurrencyToken: 'ctx-token',
  };
}

/** A "Create food images" run, holding what it is told to. */
function imagesRun(links: readonly { kind: string; resourceId: string; stepKey: string | null }[], steps?: unknown[]): WorkflowRun {
  return decodeWorkflowRun(wireRun({
    id: RUN_ID,
    definitionKey: 'images.create',
    links,
    ...(steps ? { steps } : {}),
  }))!;
}

function recipe(links: readonly Partial<RecipeAssetLink>[] = []): RecipeDetail {
  return {
    id: 'recipe-1',
    title: 'Plum galette',
    status: 'Draft',
    concurrencyToken: 'recipe-token',
    instructionGroups: [],
    assetLinks: links.map((link, index) => ({
      id: `link-${index}`,
      sortOrder: index,
      mediaAssetId: 'asset-x',
      mediaAssetVersionNumber: null,
      instructionStepId: null,
      role: 'Gallery',
      caption: null,
      ...link,
    })),
  } as unknown as RecipeDetail;
}

let port: WorkflowStepPort;
let keepDraft: jasmine.Spy<(pointer: WorkflowResourcePointer) => Promise<boolean>>;
let role: WorkspaceRole;
let getBrandProfile: jasmine.Spy;
let getRecipeDetail: jasmine.Spy;
let link: jasmine.Spy;
let getContext: jasmine.Spy;
let assetDetail: jasmine.Spy;

function configure(): void {
  port = new WorkflowStepPort();
  keepDraft = jasmine.createSpy('keepDraft').and.resolveTo(true);
  port.connect(keepDraft);
  role = 'Editor';
  getBrandProfile = jasmine.createSpy('getBrandProfile').and.resolveTo({ status: 'first_use' });
  getRecipeDetail = jasmine.createSpy('getRecipeDetail').and.resolveTo({ status: 'found', recipe: recipe() });
  link = jasmine.createSpy('link');
  getContext = jasmine.createSpy('get').and.returnValue(of({ status: 'found', context: context([]) }));
  assetDetail = jasmine.createSpy('detail').and.callFake((_slug: string, id: string) =>
    of({ status: 'found', asset: { id, title: id === 'asset-1' ? 'Galette, first slice' : 'Galette, whole' } }),
  );

  TestBed.configureTestingModule({
    providers: [
      provideRouter([]),
      { provide: WorkflowStepPort, useValue: port },
      { provide: BrandProfileService, useValue: { getBrandProfile } },
      { provide: RecipeService, useValue: { getRecipeDetail } },
      { provide: RecipeAssetLinkService, useValue: { link } },
      { provide: CreativeContextService, useValue: { get: getContext } },
      { provide: DamAssetService, useValue: { detail: assetDetail } },
      {
        provide: WorkspaceMembershipService,
        useValue: {
          state: signal({
            status: 'ready',
            memberships: [{ workspaceId: 'w1', workspaceSlug: SLUG, workspaceName: 'Sam', membershipId: 'm1', get role() { return role; }, status: 'Active' }],
          }),
          ensureLoaded: () => Promise.resolve(),
        },
      },
    ],
  });
  TestBed.overrideComponent(ImagesStudioStepComponent, {
    remove: { imports: [ImageStudioComponent] },
    add: { imports: [StubStudioComponent] },
  });
  TestBed.overrideComponent(ImagesLeadPictureStepComponent, {
    remove: { imports: [RecipeMediaComponent] },
    add: { imports: [StubRecipeMediaComponent] },
  });
}

async function render<T>(type: Type<T>, inputs: Record<string, unknown>): Promise<ComponentFixture<T>> {
  const fixture = TestBed.createComponent(type);
  for (const [name, value] of Object.entries(inputs)) fixture.componentRef.setInput(name, value);
  fixture.detectChanges();
  await settle(fixture);
  return fixture;
}

async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  for (let i = 0; i < 3; i++) {
    await fixture.whenStable();
    await new Promise((resolve) => setTimeout(resolve, 0));
    fixture.detectChanges();
  }
}

function text(fixture: ComponentFixture<unknown>): string {
  return (fixture.nativeElement as HTMLElement).textContent?.replace(/\s+/g, ' ').trim() ?? '';
}

function button(fixture: ComponentFixture<unknown>, label: string): HTMLButtonElement | null {
  return (
    Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('button')).find(
      (each) => each.textContent?.replace(/\s+/g, ' ').trim() === label,
    ) ?? null
  );
}

function studio(fixture: ComponentFixture<unknown>): StubStudioComponent {
  return fixture.debugElement.query((node) => node.componentInstance instanceof StubStudioComponent).componentInstance;
}

describe('"Create food images" step screens (13A.19)', () => {
  beforeEach(configure);

  describe('the studio parts', () => {
    it('opens the first part on new work, offering the run’s recipe and claiming nothing made yet', async () => {
      const fixture = await render(ImagesStudioStepComponent, {
        part: 'about',
        run: imagesRun([{ kind: 'Recipe', resourceId: 'recipe-1', stepKey: null }]),
        workspaceSlug: SLUG,
      });

      const shown = studio(fixture);
      expect(shown.seenPart).toBe('about');
      expect(shown.seenContextId).toBeNull();
      expect(shown.seenSuggestion).withContext('offered, never linked on arrival').toBe('recipe-1');
      expect(keepDraft).not.toHaveBeenCalled();
      expect(port.registration()!.ready!()).toBeFalse();
      expect(port.registration()!.results!()).toEqual([]);
    });

    it('keeps new work on the run the moment it is filed, and names it as what the step made', async () => {
      const fixture = await render(ImagesStudioStepComponent, { part: 'about', run: imagesRun([]), workspaceSlug: SLUG });

      studio(fixture).host.filed('ctx-9');
      await settle(fixture);

      expect(keepDraft).toHaveBeenCalledOnceWith({ kind: 'CreativeContext', id: 'ctx-9' });
      expect(port.registration()!.draft!()).toEqual({ kind: 'CreativeContext', id: 'ctx-9' });
      expect(port.registration()!.results!()).toEqual([{ kind: 'CreativeContext', id: 'ctx-9' }]);
      expect(port.registration()!.ready!()).toBeTrue();

      const elsewhere = (fixture.nativeElement as HTMLElement).querySelector('a')!;
      expect(elsewhere.getAttribute('href')).toBe(`/${SLUG}/image-studio/context/ctx-9?workflowRun=${RUN_ID}`);
    });

    it('says so when the run could not be pointed at the work, and still says the work is saved', async () => {
      keepDraft.and.resolveTo(false);
      const fixture = await render(ImagesStudioStepComponent, { part: 'about', run: imagesRun([]), workspaceSlug: SLUG });

      studio(fixture).host.filed('ctx-9');
      await settle(fixture);

      expect(text(fixture)).toContain('Your work is saved, but this workflow couldn’t be pointed at it just now');
    });

    it('opens a later part on the work the run holds, from its link or from the first step’s draft', async () => {
      let fixture = await render(ImagesStudioStepComponent, {
        part: 'plan',
        run: imagesRun([{ kind: 'CreativeContext', resourceId: 'ctx-1', stepKey: 'about' }]),
        workspaceSlug: SLUG,
      });
      expect(studio(fixture).seenContextId).toBe('ctx-1');
      expect(studio(fixture).seenSuggestion).withContext('only the first part offers a recipe').toBeNull();
      expect(port.registration()!.draft!()).withContext('a later part keeps no draft: its actions take none').toBeNull();
      fixture.destroy();

      fixture = await render(ImagesStudioStepComponent, {
        part: 'plan',
        run: imagesRun([], [
          { key: 'about', title: 'Choose', requirement: 'Required', status: 'InProgress', draftKind: 'CreativeContext', draftResourceId: 'ctx-2' },
          { key: 'plan', title: 'Plan', requirement: 'Optional', status: 'NotStarted' },
        ]),
        workspaceSlug: SLUG,
      });
      expect(studio(fixture).seenContextId).toBe('ctx-2');
    });

    it('says where the work is when a later part opens with none, and shows no studio', async () => {
      const fixture = await render(ImagesStudioStepComponent, { part: 'make', run: imagesRun([]), workspaceSlug: SLUG });

      expect(text(fixture)).toContain('Go back to the first step to choose what the picture is for.');
      expect((fixture.nativeElement as HTMLElement).querySelector('.stub-studio')).toBeNull();
    });

    it('names the pictures saved to the library as what making and keeping made, and nothing else the work names', async () => {
      const fixture = await render(ImagesStudioStepComponent, {
        part: 'make',
        run: imagesRun([{ kind: 'CreativeContext', resourceId: 'ctx-1', stepKey: 'about' }]),
        workspaceSlug: SLUG,
      });
      expect(port.registration()!.ready!()).withContext('nothing saved yet').toBeFalse();

      studio(fixture).workContext.set(context([
        reference({ id: 'ref-1', mediaAssetId: 'asset-1' }),
        reference({ id: 'ref-2', purpose: 'Source', mediaAssetId: 'asset-reference' }),
        reference({ id: 'ref-3', kind: 'Recipe', mediaAssetId: null, recipeId: 'recipe-1' }),
      ]));
      fixture.detectChanges();

      expect(port.registration()!.results!()).toEqual([{ kind: 'MediaAsset', id: 'asset-1' }]);
      expect(port.registration()!.ready!()).toBeTrue();
    });

    it('sends what is waiting before a move, and waits for the run to be pointed at the work', async () => {
      let answer!: (kept: boolean) => void;
      keepDraft.and.returnValue(new Promise((resolve) => (answer = resolve)));
      const fixture = await render(ImagesStudioStepComponent, { part: 'about', run: imagesRun([]), workspaceSlug: SLUG });
      studio(fixture).host.filed('ctx-9');

      let moved: boolean | null = null;
      void port.registration()!.flush().then((value) => (moved = value));
      await settle(fixture);
      expect(studio(fixture).flushed).toBe(1);
      expect(moved).withContext('the pointer is still on its way').toBeNull();

      answer(true);
      await settle(fixture);
      expect(moved).toBeTrue();
    });

    it('moves on when the run could not be pointed at the work, since Continue names it, and says so', async () => {
      keepDraft.and.resolveTo(false);
      const fixture = await render(ImagesStudioStepComponent, { part: 'about', run: imagesRun([]), workspaceSlug: SLUG });
      studio(fixture).host.filed('ctx-9');
      await settle(fixture);

      expect(await port.registration()!.flush()).toBeTrue();
      expect(port.registration()!.results!()).toEqual([{ kind: 'CreativeContext', id: 'ctx-9' }]);
      expect(text(fixture)).toContain('couldn’t be pointed at it just now');
    });

    it('does not move on while what the work is about could not be saved', async () => {
      const fixture = await render(ImagesStudioStepComponent, {
        part: 'about',
        run: imagesRun([{ kind: 'CreativeContext', resourceId: 'ctx-1', stepKey: 'about' }]),
        workspaceSlug: SLUG,
      });
      studio(fixture).flushAnswer = false;

      expect(await port.registration()!.flush()).toBeFalse();
    });

    it('hands the studio the brand’s look and picture count, and carries on without either', async () => {
      getBrandProfile.and.resolveTo({ status: 'found', profile: { channelDefaults: ['instagram'], defaultPictureCount: 3 } });
      let fixture = await render(ImagesStudioStepComponent, { part: 'about', run: imagesRun([]), workspaceSlug: SLUG });
      expect(studio(fixture).host.brand()).toEqual({ status: 'ready', channelKeys: ['instagram'], pictureCount: 3 });
      fixture.destroy();

      getBrandProfile.and.resolveTo({ status: 'unavailable' });
      fixture = await render(ImagesStudioStepComponent, { part: 'about', run: imagesRun([]), workspaceSlug: SLUG });
      expect(studio(fixture).host.brand().status).toBe('unavailable');
      expect((fixture.nativeElement as HTMLElement).querySelector('.stub-studio')).not.toBeNull();
    });
  });

  describe('the lead picture', () => {
    const withWork = () =>
      imagesRun([
        { kind: 'Recipe', resourceId: 'recipe-1', stepKey: null },
        { kind: 'CreativeContext', resourceId: 'ctx-1', stepKey: 'about' },
      ]);

    beforeEach(() => {
      getContext.and.returnValue(of({
        status: 'found',
        context: context([reference({ id: 'ref-1', mediaAssetId: 'asset-1' }), reference({ id: 'ref-2', mediaAssetId: 'asset-2' })]),
      }));
    });

    it('offers each picture this run saved as the lead picture, by its own title', async () => {
      const fixture = await render(ImagesLeadPictureStepComponent, { run: withWork(), workspaceSlug: SLUG });

      expect(button(fixture, 'Use “Galette, first slice” as the lead picture')?.classList).toContain('cp-button--primary');
      expect(button(fixture, 'Use “Galette, whole” as the lead picture')).not.toBeNull();
      expect(port.registration()!.ready!()).toBeFalse();
      expect((fixture.nativeElement as HTMLElement).querySelector('.stub-media')).withContext('the recipe’s own panel, too').not.toBeNull();
    });

    it('links the picture as the lead picture in one press, and is then done', async () => {
      link.and.resolveTo({ status: 'linked', recipe: recipe([{ role: 'Hero', mediaAssetId: 'asset-1' }]), replayed: false });
      const announced = spyOn(port, 'announce');
      const fixture = await render(ImagesLeadPictureStepComponent, { run: withWork(), workspaceSlug: SLUG });

      button(fixture, 'Use “Galette, first slice” as the lead picture')!.click();
      await settle(fixture);

      expect(link).toHaveBeenCalledOnceWith(
        SLUG,
        'recipe-1',
        jasmine.objectContaining({ mediaAssetId: 'asset-1', role: 'Hero', expectedConcurrencyToken: 'recipe-token' }),
        jasmine.any(String),
      );
      expect(text(fixture)).toContain('Your recipe has a lead picture.');
      expect(port.registration()!.ready!()).toBeTrue();
      expect(announced).toHaveBeenCalledWith('“Galette, first slice” is now the lead picture of Plum galette.');
    });

    it('takes one press while the link is being made, and retries a lost answer under the same key', async () => {
      let answer!: (value: unknown) => void;
      link.and.returnValue(new Promise((resolve) => (answer = resolve)));
      const fixture = await render(ImagesLeadPictureStepComponent, { run: withWork(), workspaceSlug: SLUG });

      button(fixture, 'Use “Galette, first slice” as the lead picture')!.click();
      button(fixture, 'Use “Galette, first slice” as the lead picture')!.click();
      expect(link).toHaveBeenCalledTimes(1);

      answer({ status: 'unavailable' });
      await settle(fixture);
      expect(text(fixture)).toContain('Trying again is safe.');

      link.and.resolveTo({ status: 'linked', recipe: recipe([{ role: 'Hero', mediaAssetId: 'asset-1' }]), replayed: true });
      button(fixture, 'Use “Galette, first slice” as the lead picture')!.click();
      await settle(fixture);

      expect(link.calls.argsFor(1)[3]).toBe(link.calls.argsFor(0)[3]);
    });

    it('reads the recipe again when it moved on, so what it offers is true', async () => {
      link.and.resolveTo({ status: 'hero_exists' });
      const fixture = await render(ImagesLeadPictureStepComponent, { run: withWork(), workspaceSlug: SLUG });
      getRecipeDetail.and.resolveTo({ status: 'found', recipe: recipe([{ role: 'Hero', mediaAssetId: 'asset-other' }]) });

      button(fixture, 'Use “Galette, first slice” as the lead picture')!.click();
      await settle(fixture);

      expect(getRecipeDetail).toHaveBeenCalledTimes(2);
      expect(text(fixture)).toContain('Your recipe already has a lead picture.');
      expect(text(fixture)).toContain('Your recipe has a lead picture.');
      expect(button(fixture, 'Use “Galette, first slice” as the lead picture')).toBeNull();
    });

    it('offers nothing when the recipe has a lead picture already, and counts as done', async () => {
      getRecipeDetail.and.resolveTo({ status: 'found', recipe: recipe([{ role: 'Hero', mediaAssetId: 'asset-old' }]) });
      const fixture = await render(ImagesLeadPictureStepComponent, { run: withWork(), workspaceSlug: SLUG });

      expect(text(fixture)).toContain('Your recipe has a lead picture.');
      expect(button(fixture, 'Use “Galette, first slice” as the lead picture')).toBeNull();
      expect(port.registration()!.ready!()).toBeTrue();
    });

    it('offers no picture it could not read, nor one the recipe already uses', async () => {
      getRecipeDetail.and.resolveTo({ status: 'found', recipe: recipe([{ role: 'Gallery', mediaAssetId: 'asset-2' }]) });
      assetDetail.and.callFake((_slug: string, id: string) =>
        id === 'asset-1' ? of({ status: 'not_found' }) : of({ status: 'found', asset: { id, title: 'Galette, whole' } }),
      );
      const fixture = await render(ImagesLeadPictureStepComponent, { run: withWork(), workspaceSlug: SLUG });

      expect(Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('button')).some((each) => each.textContent?.includes('as the lead picture'))).toBeFalse();
    });

    it('offers no shortcut to a Viewer, and says why', async () => {
      role = 'Viewer';
      const fixture = await render(ImagesLeadPictureStepComponent, { run: withWork(), workspaceSlug: SLUG });

      expect(button(fixture, 'Use “Galette, first slice” as the lead picture')).toBeNull();
      expect(text(fixture)).toContain('You have view-only access, so you can’t change this recipe’s pictures.');
    });

    it('says the pictures this run saved could not be read, rather than offering none in silence', async () => {
      getContext.and.returnValue(of({ status: 'unavailable' }));
      const fixture = await render(ImagesLeadPictureStepComponent, { run: withWork(), workspaceSlug: SLUG });

      expect(text(fixture)).toContain('The pictures you saved in this workflow couldn’t be read just now');
      expect((fixture.nativeElement as HTMLElement).querySelector('.stub-media')).withContext('the library is still there').not.toBeNull();
    });

    it('says the recipe is gone, or could not be read, with a way to read it again', async () => {
      getRecipeDetail.and.resolveTo({ status: 'not_found' });
      let fixture = await render(ImagesLeadPictureStepComponent, { run: withWork(), workspaceSlug: SLUG });
      expect(text(fixture)).toContain('can’t be found any more');
      fixture.destroy();

      getRecipeDetail.and.resolveTo({ status: 'unavailable' });
      fixture = await render(ImagesLeadPictureStepComponent, { run: withWork(), workspaceSlug: SLUG });
      expect(text(fixture)).toContain('Your recipe couldn’t be read just now.');

      getRecipeDetail.and.resolveTo({ status: 'found', recipe: recipe() });
      button(fixture, 'Try again')!.click();
      await settle(fixture);
      expect(button(fixture, 'Use “Galette, first slice” as the lead picture')).not.toBeNull();
    });

    // AU.7: the recipe arriving is drawn as lines and said once, from a status that is always there.
    it('draws lines while the recipe is read, says it once, and empties the status once it is', async () => {
      getRecipeDetail.and.returnValue(new Promise(() => undefined));
      const held = await render(ImagesLeadPictureStepComponent, { run: withWork(), workspaceSlug: SLUG });
      const root = (fixture: ComponentFixture<unknown>) => fixture.nativeElement as HTMLElement;
      const statuses = (fixture: ComponentFixture<unknown>) =>
        Array.from(root(fixture).querySelectorAll('p.cp-sr-only[role="status"]')).map((each) => each.textContent!.trim());

      expect(statuses(held)).toEqual(['Reading your recipe…']);
      expect(root(held).querySelector('[aria-busy="true"] cp-skeleton')).not.toBeNull();
      expect(Array.from(root(held).querySelectorAll('p:not(.cp-sr-only)')).some((each) => each.textContent?.includes('Reading your recipe…'))).toBeFalse();
      held.destroy();

      getRecipeDetail.and.resolveTo({ status: 'found', recipe: recipe() });
      const read = await render(ImagesLeadPictureStepComponent, { run: withWork(), workspaceSlug: SLUG });
      expect(statuses(read)).toEqual(['']);
      expect(root(read).querySelector('[aria-busy="true"] cp-skeleton')).toBeNull();
    });
  });
});
