import { Component, Type, inject, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';

import { ChannelPostPackage } from '../../../models/channel-post.models';
import { ContentCardDetail } from '../../../models/content-board.models';
import { CreativeContext, CreativeContextReference } from '../../../models/creative-context.models';
import { WorkflowResourcePointer, WorkflowRun, decodeWorkflowRun } from '../../../models/workflow-run.models';
import { wireRun } from '../../../models/workflow-run.testing';
import { BrandStyleGuideService } from '../../../services/brand-style-guide.service';
import { ChannelPostService } from '../../../services/channel-post.service';
import { ContentBoardService } from '../../../services/content-board.service';
import { CreativeContextService } from '../../../services/creative-context.service';
import { WorkspaceMembershipService } from '../../../services/workspace-membership.service';
import { WorkflowStepPort } from '../../../shared/workflow-shell/workflow-step-port';
import { WorkspaceRole } from '../../../models/auth.models';
import { SocialStudioComponent } from '../../social-studio/social-studio.component';
import { SocialStudioHost } from '../../social-studio/social-studio-host';
import { SocialBoardStepComponent } from './social-board-step.component';
import { SocialStudioStepComponent } from './social-studio-step.component';

const SLUG = 'sams-kitchen';
const RUN_ID = '1f6f2a54-7c1e-4d4b-9a51-2f1b3c4d5e6f';

/** The studio, stood in for: what the step reads of it, and what it saw of the host it was given. */
@Component({
  selector: 'cp-social-studio',
  template: `<p class="stub-studio">{{ seenPart }}</p>`,
  providers: [{ provide: SocialStudioComponent, useExisting: StubStudioComponent }],
})
class StubStudioComponent {
  readonly host = inject(SocialStudioHost);
  readonly seenPart = this.host.part;
  readonly seenContextId = this.host.contextId;
  readonly seenSuggestion = this.host.suggestedRecipeId;
  readonly workContext = signal<CreativeContext | null>(null);
  readonly sending = signal(false);
  readonly hasUnsavedWords = signal(false);
  readonly anyPostKept = signal(false);
  flushed = 0;

  flushWork(): Promise<boolean> {
    this.flushed += 1;
    return Promise.resolve(true);
  }
}

function reference(overrides: Partial<CreativeContextReference>): CreativeContextReference {
  return {
    id: 'ref-1',
    kind: 'Recipe',
    purpose: 'Source',
    sortOrder: 0,
    recipeId: 'recipe-1',
    recipeVersionId: 'version-3',
    conceptRequestId: null,
    conceptId: null,
    mediaAssetId: null,
    mediaAssetVersionNumber: null,
    generatedImageId: null,
    promptRecordId: null,
    addedAt: '2026-10-15T10:00:00Z',
    ...overrides,
  };
}

function context(overrides: Partial<CreativeContext> = {}): CreativeContext {
  return {
    id: 'ctx-1',
    workingTitle: 'Plum galette week',
    pictureBrief: null,
    briefSource: null,
    workingBrief: null,
    channelKeys: [],
    day: null,
    weeklyThemeKey: null,
    references: [
      reference({}),
      reference({
        id: 'ref-2',
        kind: 'DamAsset',
        recipeId: null,
        recipeVersionId: null,
        mediaAssetId: 'asset-1',
        mediaAssetVersionNumber: 2,
      }),
    ],
    createdAt: '2026-10-15T09:00:00Z',
    updatedAt: '2026-10-15T10:00:00Z',
    archivedAt: null,
    concurrencyToken: 'ctx-token',
    ...overrides,
  };
}

function postsPackage(statuses: readonly string[]): ChannelPostPackage {
  return {
    id: 'pkg-1',
    creativeContextId: 'ctx-1',
    channels: statuses.map((status, index) => ({ channelKey: `channel-${index}`, status })),
    updatedAt: '2026-10-15T10:00:00Z',
  } as unknown as ChannelPostPackage;
}

function card(id = 'card-1'): ContentCardDetail {
  return {
    card: { id, title: 'Plum galette week', column: 'ToDo' },
    description: null,
    lineage: [],
    history: [],
    historyTruncated: false,
  } as unknown as ContentCardDetail;
}

function socialRun(links: readonly { kind: string; resourceId: string; stepKey: string | null }[]): WorkflowRun {
  return decodeWorkflowRun(wireRun({ id: RUN_ID, definitionKey: 'social.create', links }))!;
}

const withWork = () => socialRun([{ kind: 'CreativeContext', resourceId: 'ctx-1', stepKey: 'about' }]);

let port: WorkflowStepPort;
let keepDraft: jasmine.Spy<(pointer: WorkflowResourcePointer) => Promise<boolean>>;
let getActive: jasmine.Spy;
let getContext: jasmine.Spy;
let readPackage: jasmine.Spy;
let create: jasmine.Spy;
let detail: jasmine.Spy;
let role: WorkspaceRole;

function configure(): void {
  role = 'Editor';
  try {
    sessionStorage.clear();
  } catch {
    // Nothing to clear.
  }
  port = new WorkflowStepPort();
  keepDraft = jasmine.createSpy('keepDraft').and.resolveTo(true);
  port.connect(keepDraft);
  getActive = jasmine.createSpy('getActive').and.resolveTo({ status: 'none' });
  getContext = jasmine.createSpy('get').and.returnValue(of({ status: 'found', context: context() }));
  readPackage = jasmine
    .createSpy('readPackage')
    .and.resolveTo({ status: 'found', package: postsPackage(['Accepted', 'Proposed']) });
  create = jasmine.createSpy('create').and.returnValue(of({ status: 'saved', detail: card() }));
  detail = jasmine.createSpy('detail').and.returnValue(of({ status: 'found', detail: card() }));

  TestBed.configureTestingModule({
    providers: [
      provideRouter([]),
      { provide: WorkflowStepPort, useValue: port },
      { provide: BrandStyleGuideService, useValue: { getActive } },
      { provide: CreativeContextService, useValue: { get: getContext } },
      { provide: ChannelPostService, useValue: { readPackage } },
      { provide: ContentBoardService, useValue: { create, detail } },
      {
        provide: WorkspaceMembershipService,
        useValue: {
          state: signal({
            status: 'ready',
            memberships: [
              {
                workspaceId: 'w1',
                workspaceSlug: SLUG,
                workspaceName: 'Sam',
                membershipId: 'm1',
                get role() {
                  return role;
                },
                status: 'Active',
              },
            ],
          }),
          ensureLoaded: () => Promise.resolve(),
        },
      },
    ],
  });
  TestBed.overrideComponent(SocialStudioStepComponent, {
    remove: { imports: [SocialStudioComponent] },
    add: { imports: [StubStudioComponent] },
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

describe('"Create social posts" step screens (13A.20)', () => {
  beforeEach(configure);

  describe('the studio parts', () => {
    it('names the guide the posts are written in, as the server reads it, with where to change it', async () => {
      getActive.and.resolveTo({
        status: 'found',
        voice: { guideId: 'guide-1', displayName: 'Sam’s kitchen voice', versionId: 'gv-2', versionNumber: 2 },
      });
      const fixture = await render(SocialStudioStepComponent, {
        part: 'about',
        run: socialRun([]),
        workspaceSlug: SLUG,
      });

      expect(text(fixture)).toContain('Your posts are written in your brand guide, “Sam’s kitchen voice”, version 2');
      expect((fixture.nativeElement as HTMLElement).querySelector('cp-notice a')?.getAttribute('href')).toBe(
        `/${SLUG}/brand/style-guide/guide-1`,
      );
    });

    it('says so when no guide is switched on, and carries on', async () => {
      const fixture = await render(SocialStudioStepComponent, {
        part: 'about',
        run: socialRun([]),
        workspaceSlug: SLUG,
      });

      expect(text(fixture)).toContain('You haven’t switched on a brand guide');
      expect((fixture.nativeElement as HTMLElement).querySelector('.stub-studio')).not.toBeNull();
    });

    it('does not read the guide on a later part', async () => {
      await render(SocialStudioStepComponent, { part: 'write', run: withWork(), workspaceSlug: SLUG });

      expect(getActive).not.toHaveBeenCalled();
    });

    it('keeps new work on the run the moment it is filed, and names it as what the first step made', async () => {
      const fixture = await render(SocialStudioStepComponent, {
        part: 'about',
        run: socialRun([]),
        workspaceSlug: SLUG,
      });
      expect(port.registration()!.ready!()).toBeFalse();

      studio(fixture).host.filed('ctx-9');
      await settle(fixture);

      expect(keepDraft).toHaveBeenCalledOnceWith({ kind: 'CreativeContext', id: 'ctx-9' });
      expect(port.registration()!.results!()).toEqual([{ kind: 'CreativeContext', id: 'ctx-9' }]);
      expect(port.registration()!.ready!()).toBeTrue();
      // The studio goes the same steps, so the link opens it on this one; it no longer promises one page.
      const elsewhere = (fixture.nativeElement as HTMLElement).querySelector('p.elsewhere');
      expect(elsewhere?.querySelector('a')?.getAttribute('href')).toBe(
        `/${SLUG}/social-studio/context/ctx-9?workflowRun=${RUN_ID}&step=about`,
      );
      expect(elsewhere?.textContent).not.toContain('one page');
    });

    it('opens the later parts on the work the run holds, and offers the run’s recipe on the first part only', async () => {
      let fixture = await render(SocialStudioStepComponent, {
        part: 'about',
        run: socialRun([{ kind: 'Recipe', resourceId: 'recipe-1', stepKey: null }]),
        workspaceSlug: SLUG,
      });
      expect(studio(fixture).seenSuggestion).toBe('recipe-1');
      fixture.destroy();

      fixture = await render(SocialStudioStepComponent, { part: 'write', run: withWork(), workspaceSlug: SLUG });
      expect(studio(fixture).seenPart).toBe('write');
      expect(studio(fixture).seenContextId).toBe('ctx-1');
      expect(port.registration()!.draft!())
        .withContext('a later part keeps no draft: its actions take none')
        .toBeNull();
      expect(studio(fixture).seenSuggestion).toBeNull();
    });

    it('is ready to move on from writing once a post is kept, and says edited words are not saved yet', async () => {
      const fixture = await render(SocialStudioStepComponent, { part: 'write', run: withWork(), workspaceSlug: SLUG });
      expect(port.registration()!.ready!()).toBeFalse();

      studio(fixture).anyPostKept.set(true);
      studio(fixture).hasUnsavedWords.set(true);
      fixture.detectChanges();

      expect(port.registration()!.ready!()).toBeTrue();
      expect(port.registration()!.unsaved()).toBeTrue();
      expect(port.registration()!.results!()).withContext('the server reads the posts from the work').toEqual([]);
    });

    it('sends what is waiting before a move', async () => {
      const fixture = await render(SocialStudioStepComponent, {
        part: 'picture',
        run: withWork(),
        workspaceSlug: SLUG,
      });

      expect(await port.registration()!.flush()).toBeTrue();
      expect(studio(fixture).flushed).toBe(1);
    });

    it('says where the work is when a later part opens with none', async () => {
      const fixture = await render(SocialStudioStepComponent, {
        part: 'write',
        run: socialRun([]),
        workspaceSlug: SLUG,
      });

      expect(text(fixture)).toContain('Go back to the first step to say what the posts are about.');
      expect((fixture.nativeElement as HTMLElement).querySelector('.stub-studio')).toBeNull();
    });
  });

  describe('the board', () => {
    it('says what the card will link before anything is added, and that nothing is scheduled', async () => {
      const fixture = await render(SocialBoardStepComponent, { run: withWork(), workspaceSlug: SLUG });

      expect(text(fixture)).toContain('One card goes in To do');
      expect(text(fixture)).toContain('Nothing is scheduled or posted.');
      expect(text(fixture)).toContain('Plum galette week');
      expect(text(fixture)).toContain('Posts you keptOne');
      expect(text(fixture)).toContain('PictureThe one you chose');
      expect(create).not.toHaveBeenCalled();
      expect(port.registration()!.ready!()).toBeFalse();
    });

    it('adds one card linking the work, its recipe, its posts and its picture, and keeps it on the run', async () => {
      const announced = spyOn(port, 'announce');
      const fixture = await render(SocialBoardStepComponent, { run: withWork(), workspaceSlug: SLUG });

      button(fixture, 'Add to your board')!.click();
      await settle(fixture);

      expect(create).toHaveBeenCalledOnceWith(
        SLUG,
        { title: 'Plum galette week', description: null, channelKey: null, plannedDate: null },
        'ToDo',
        jasmine.any(String),
        {
          creativeContextId: 'ctx-1',
          recipeId: 'recipe-1',
          recipeVersionId: 'version-3',
          mediaAssetId: 'asset-1',
          mediaAssetVersionNumber: 2,
          socialPackageId: 'pkg-1',
        },
      );
      expect(keepDraft).toHaveBeenCalledOnceWith({ kind: 'ContentCard', id: 'card-1' });
      expect(port.registration()!.results!()).toEqual([{ kind: 'ContentCard', id: 'card-1' }]);
      expect(port.registration()!.ready!()).toBeTrue();
      expect(text(fixture)).toContain(
        'On your board as “Plum galette week”, in To do. Nothing is scheduled or posted yet.',
      );
      expect(announced).toHaveBeenCalledWith('Added to your board as “Plum galette week”.');
    });

    it('sends the same request again after a lost answer, a second press or a reload, so it never makes two', async () => {
      create.and.returnValue(of({ status: 'unavailable' }));
      let fixture = await render(SocialBoardStepComponent, { run: withWork(), workspaceSlug: SLUG });

      button(fixture, 'Add to your board')!.click();
      await settle(fixture);
      expect(text(fixture)).toContain('Trying again is safe — it won’t make two.');
      button(fixture, 'Add to your board')!.click();
      await settle(fixture);
      fixture.destroy();

      fixture = await render(SocialBoardStepComponent, { run: withWork(), workspaceSlug: SLUG });
      button(fixture, 'Add to your board')!.click();
      await settle(fixture);

      const keys = create.calls.allArgs().map((args) => args[3]);
      expect(keys.length).toBe(3);
      expect(new Set(keys).size).withContext('one request, sent three times').toBe(1);
    });

    it('shows the card the run already holds rather than offering another', async () => {
      const fixture = await render(SocialBoardStepComponent, {
        run: socialRun([
          { kind: 'CreativeContext', resourceId: 'ctx-1', stepKey: 'about' },
          { kind: 'ContentCard', resourceId: 'card-1', stepKey: 'board' },
        ]),
        workspaceSlug: SLUG,
      });

      expect(detail).toHaveBeenCalledWith(SLUG, 'card-1');
      expect(text(fixture)).toContain('On your board as “Plum galette week”');
      expect(button(fixture, 'Add to your board')).toBeNull();
      expect(port.registration()!.ready!()).toBeTrue();
    });

    it('says the card could not be read, rather than offering a second, when the one the run holds is unreachable', async () => {
      detail.and.returnValue(of({ status: 'unavailable' }));
      const fixture = await render(SocialBoardStepComponent, {
        run: socialRun([
          { kind: 'CreativeContext', resourceId: 'ctx-1', stepKey: 'about' },
          { kind: 'ContentCard', resourceId: 'card-1', stepKey: 'board' },
        ]),
        workspaceSlug: SLUG,
      });

      expect(text(fixture)).toContain('Your posts couldn’t be read just now.');
      expect(button(fixture, 'Add to your board')).toBeNull();

      detail.and.returnValue(of({ status: 'found', detail: card() }));
      button(fixture, 'Try again')!.click();
      await settle(fixture);
      expect(text(fixture)).toContain('On your board as “Plum galette week”');
    });

    it('offers the card again when the one the run held is gone', async () => {
      detail.and.returnValue(of({ status: 'not_found' }));
      const fixture = await render(SocialBoardStepComponent, {
        run: socialRun([
          { kind: 'CreativeContext', resourceId: 'ctx-1', stepKey: 'about' },
          { kind: 'ContentCard', resourceId: 'card-gone', stepKey: 'board' },
        ]),
        workspaceSlug: SLUG,
      });

      expect(button(fixture, 'Add to your board')).not.toBeNull();
    });

    it('says plainly when no post is kept yet, and still lets the card be added', async () => {
      readPackage.and.resolveTo({ status: 'found', package: null });
      getContext.and.returnValue(of({ status: 'found', context: context({ references: [], workingTitle: null }) }));
      const fixture = await render(SocialBoardStepComponent, { run: withWork(), workspaceSlug: SLUG });

      expect(text(fixture)).toContain('You haven’t kept a post yet.');
      expect(text(fixture)).toContain('CalledSocial posts');
      button(fixture, 'Add to your board')!.click();
      await settle(fixture);

      expect(create.calls.mostRecent().args[4]).toEqual(
        jasmine.objectContaining({ socialPackageId: null, mediaAssetId: null, recipeId: null }),
      );
    });

    it('says a refusal in a sentence, and that the work could not be read with a way to read it again', async () => {
      create.and.returnValue(of({ status: 'refused', errors: {} }));
      let fixture = await render(SocialBoardStepComponent, { run: withWork(), workspaceSlug: SLUG });
      button(fixture, 'Add to your board')!.click();
      await settle(fixture);
      expect(text(fixture)).toContain('Something this work names can’t go on a card any more');
      fixture.destroy();

      getContext.and.returnValue(of({ status: 'unavailable' }));
      fixture = await render(SocialBoardStepComponent, { run: withWork(), workspaceSlug: SLUG });
      expect(text(fixture)).toContain('Your posts couldn’t be read just now.');

      getContext.and.returnValue(of({ status: 'found', context: context() }));
      button(fixture, 'Try again')!.click();
      await settle(fixture);
      expect(button(fixture, 'Add to your board')).not.toBeNull();
    });

    it('offers a Viewer nothing to add, and says why', async () => {
      role = 'Viewer';
      const fixture = await render(SocialBoardStepComponent, { run: withWork(), workspaceSlug: SLUG });

      expect(button(fixture, 'Add to your board')).toBeNull();
      expect(text(fixture)).toContain('You have view-only access, so you can’t add cards to the board.');
    });

    it('starts a new request after a key the server says was used for another, and keeps it after a stale answer', async () => {
      create.and.returnValue(of({ status: 'stale' }));
      const fixture = await render(SocialBoardStepComponent, { run: withWork(), workspaceSlug: SLUG });

      button(fixture, 'Add to your board')!.click();
      await settle(fixture);
      expect(text(fixture)).toContain('The board changed while this was being added. Trying again is safe.');

      create.and.returnValue(of({ status: 'key_reused' }));
      button(fixture, 'Add to your board')!.click();
      await settle(fixture);

      create.and.returnValue(of({ status: 'saved', detail: card() }));
      button(fixture, 'Add to your board')!.click();
      await settle(fixture);

      const keys = create.calls.allArgs().map((args) => args[3]);
      expect(keys[1]).withContext('a stale answer keeps the request').toBe(keys[0]);
      expect(keys[2]).withContext('a spent key is not sent again').not.toBe(keys[1]);
    });

    it('says so when the card is made but the run could not be pointed at it', async () => {
      keepDraft.and.resolveTo(false);
      const fixture = await render(SocialBoardStepComponent, { run: withWork(), workspaceSlug: SLUG });

      button(fixture, 'Add to your board')!.click();
      await settle(fixture);

      expect(text(fixture)).toContain(
        'The card is on your board, but this workflow couldn’t be pointed at it just now.',
      );
      expect(port.registration()!.results!())
        .withContext('Continue still names it')
        .toEqual([{ kind: 'ContentCard', id: 'card-1' }]);
    });

    it('says there is nothing to add when the run holds no work', async () => {
      const fixture = await render(SocialBoardStepComponent, { run: socialRun([]), workspaceSlug: SLUG });

      expect(text(fixture)).toContain('There is nothing to put on the board yet.');
      expect(
        (fixture.nativeElement as HTMLElement)
          .querySelector('cp-empty-state cp-illustration')
          ?.getAttribute('data-name'),
      ).toBe('board');
      expect(button(fixture, 'Add to your board')).toBeNull();
    });
  });
});
