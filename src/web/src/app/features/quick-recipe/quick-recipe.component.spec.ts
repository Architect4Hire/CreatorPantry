import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ConfirmService } from '../../core/confirm.service';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { NEVER, Observable, of } from 'rxjs';

import { WorkspaceRole } from '../../models/auth.models';
import { AiProposalDetail, AiProposalStatus, AiProposedChange } from '../../models/ai-proposal.models';
import { GeneratedImageOperationDetail } from '../../models/generated-image.models';
import { AiRequestOutcome, AiWatchOperationOutcome } from '../../services/ai-request';
import { AiAllowanceState, AiUsageService } from '../../services/ai-usage.service';
import { DamAssetService } from '../../services/dam-asset.service';
import { GeneratedImageService, GeneratedImageWatchOutcome } from '../../services/generated-image.service';
import { ImagePromptService } from '../../services/image-prompt.service';
import { PhotographyConceptService } from '../../services/photography-concept.service';
import { RecipeAssetLinkService } from '../../services/recipe-asset-link.service';
import { RecipeDraftService } from '../../services/recipe-draft.service';
import { RecipeService } from '../../services/recipe.service';
import { MyMembershipsState, WorkspaceMembershipService } from '../../services/workspace-membership.service';
import { QuickRecipeComponent } from './quick-recipe.component';

@Component({ selector: 'cp-test-elsewhere', template: '<p>somewhere else</p>' })
class ElsewhereComponent {}

const SLUG = 'cozy-fall';
const OTHER_SLUG = 'other-kitchen';
const delay = (ms: number): Promise<void> => new Promise((resolve) => setTimeout(resolve, ms));

function member(workspaceId: string, slug: string, role: WorkspaceRole) {
  return { workspaceId, workspaceSlug: slug, workspaceName: slug, membershipId: `m-${workspaceId}`, role, status: 'Active' as const };
}

function row(overrides: Partial<AiProposedChange>): AiProposedChange {
  return {
    changeId: 'c1',
    changeKind: 'Set',
    targetKind: 'Recipe',
    targetId: null,
    fieldName: null,
    beforeValue: null,
    afterValue: null,
    proposedPosition: null,
    disposition: 'Pending',
    ...overrides,
  };
}

function proposal(changes: readonly AiProposedChange[], warnings: AiProposalDetail['warnings'] = []): AiProposalDetail {
  return {
    proposalId: 'p-1',
    outputSchemaVersion: '1',
    promptTemplateId: 't',
    promptTemplateVersion: '1',
    promptTemplateBodyChecksum: 'abc',
    providerName: 'provider',
    modelName: 'model',
    createdAt: '2026-10-12T12:00:05Z',
    changes,
    warnings,
  };
}

function operation(
  id: string,
  detail: AiProposalDetail | null,
  status: AiProposalStatus['status'] = 'Proposed',
): AiProposalStatus {
  return {
    aiProposalRequestId: id,
    status,
    taskType: 'RecipeFirstDraft',
    scope: 'Unspecified',
    sourceVersionId: null,
    requestedAt: '2026-10-12T12:00:00Z',
    statusChangedAt: '2026-10-12T12:00:05Z',
    failureCategory: status === 'Failed' ? 'Provider' : null,
    proposal: detail,
  };
}

const DRAFT = proposal(
  [
    row({ changeId: 'd-title', fieldName: 'title', afterValue: 'Miso Butter Corn' }),
    row({ changeId: 'd-yield', fieldName: 'yieldText', afterValue: 'Serves 4' }),
    row({ changeId: 'd-group', changeKind: 'Add', targetKind: 'IngredientGroup', targetId: 'g1' }),
    row({ changeId: 'd-line', changeKind: 'Add', targetKind: 'Ingredient', targetId: 'i1', afterValue: '4 ears corn' }),
    row({ changeId: 'd-line-name', targetKind: 'Ingredient', targetId: 'i1', fieldName: 'ingredientNameText', afterValue: 'corn' }),
    row({ changeId: 'd-line-2', changeKind: 'Add', targetKind: 'Ingredient', targetId: 'i2', afterValue: '2 tbsp white miso' }),
    row({ changeId: 'd-method', changeKind: 'Add', targetKind: 'InstructionGroup', targetId: 'g2' }),
    row({ changeId: 'd-step', changeKind: 'Add', targetKind: 'InstructionStep', targetId: 's1', afterValue: 'Char the corn.' }),
  ],
  [{ kind: 'SafetyCaution', message: 'Hot butter spits.', changeId: null }],
);
const DRAFT_CHANGE_IDS = DRAFT.changes.map((change) => change.changeId);

const LOOKS = proposal([
  row({ changeId: 'a1', changeKind: 'Add', targetKind: 'PhotographyConcept', targetId: 'look-1', afterValue: 'Warm', proposedPosition: 0 }),
  row({ targetKind: 'PhotographyConcept', targetId: 'look-1', fieldName: 'shot.DetailShot.framing', afterValue: 'Macro' }),
  row({ targetKind: 'PhotographyConcept', targetId: 'look-1', fieldName: 'shot.Hero.framing', afterValue: 'Overhead' }),
]);

const PROMPT = proposal([
  row({ changeId: 'p-add', changeKind: 'Add', targetKind: 'ImagePrompt', targetId: 'ip', afterValue: 'Charred corn, overhead.' }),
  row({ targetKind: 'ImagePrompt', targetId: 'ip', fieldName: 'avoid', afterValue: 'hands; text' }),
]);

function imageRun(status: GeneratedImageOperationDetail['status'], failureCategory: string | null = null) {
  return {
    id: 'op-1',
    status,
    variantCount: 1,
    stagedCount: status === 'Succeeded' ? 1 : 0,
    providerName: 'p',
    modelName: 'm',
    failureCategory,
    failureSummary: null,
    requestedAt: '2026-10-12T12:00:00Z',
    completedAt: '2026-10-12T12:00:20Z',
    images:
      status === 'Succeeded'
        ? [
            {
              id: 'img-1',
              variantIndex: 1,
              status: 'Staged',
              mediaType: 'image/png',
              width: 1024,
              height: 1024,
              sizeBytes: 10,
              retentionExpiresAt: '2026-10-26T12:00:00Z',
              createdAt: '2026-10-12T12:00:20Z',
            },
          ]
        : [],
  } as unknown as GeneratedImageOperationDetail;
}

interface Scenario {
  role?: WorkspaceRole;
  url?: string;
  draftOutcome?: AiRequestOutcome;
  draftWatch?: () => Observable<AiWatchOperationOutcome>;
  looksOutcome?: AiRequestOutcome;
  imageWatch?: GeneratedImageWatchOutcome;
  accept?: unknown;
  keep?: unknown;
  heroAlready?: boolean;
  link?: unknown;
}

let harness: RouterTestingHarness;
let calls: string[];
let sent: Record<string, unknown[]>;
let confirmAnswer: boolean;
let confirmRequests: { title: string }[];

async function settle(): Promise<void> {
  for (let i = 0; i < 6; i += 1) {
    await delay(0);
    harness.detectChanges();
  }
}

async function create(scenario: Scenario = {}): Promise<void> {
  calls = [];
  sent = {};
  confirmRequests = [];
  confirmAnswer = true;
  const note = (name: string, ...args: unknown[]): void => {
    calls.push(name);
    sent[name] = args;
  };
  const accepted = (op: AiProposalStatus): AiRequestOutcome => ({ status: 'accepted', operation: op, replayed: false });

  TestBed.resetTestingModule();
  await TestBed.configureTestingModule({
    providers: [
      {
        provide: ConfirmService,
        useValue: {
          confirm: (request: { title: string }) => {
            confirmRequests.push(request);
            return Promise.resolve(confirmAnswer);
          },
        },
      },
      provideRouter([
        { path: ':workspaceSlug/workflows/quick-recipe', component: QuickRecipeComponent, data: { title: 'Quick Recipe' } },
        { path: ':workspaceSlug/recipes/:recipeId', component: ElsewhereComponent },
        { path: '**', component: ElsewhereComponent },
      ]),
      {
        provide: WorkspaceMembershipService,
        useValue: {
          state: signal<MyMembershipsState>({
            status: 'ready',
            memberships: [member('w1', SLUG, scenario.role ?? 'Editor'), member('w2', OTHER_SLUG, 'Editor')],
          }),
          ensureLoaded: () => Promise.resolve(),
          load: () => Promise.resolve(),
        },
      },
      {
        provide: AiUsageService,
        useValue: {
          allowance: signal<AiAllowanceState>({ kind: 'unknown' }),
          ensureLoaded: () => Promise.resolve(),
          refresh: () => Promise.resolve(),
        },
      },
      {
        provide: RecipeDraftService,
        useValue: {
          requestDraftForDish: (slug: string, name: string, key: string) => {
            note('draft', slug, name, key);
            return Promise.resolve(scenario.draftOutcome ?? accepted(operation('r-draft', DRAFT)));
          },
          watchStatus: (slug: string, id: string) => {
            note('draft.watch', slug, id);
            return scenario.draftWatch?.() ?? of<AiWatchOperationOutcome>({ status: 'found', operation: operation(id, DRAFT) });
          },
          acceptDraft: (slug: string, id: string, body: unknown) => {
            note('accept', slug, id, body);
            return Promise.resolve(scenario.accept ?? { status: 'accepted', recipeId: 'recipe-1', replayed: false });
          },
        },
      },
      {
        provide: PhotographyConceptService,
        useValue: {
          request: (slug: string, body: unknown, key: string) => {
            note('looks', slug, body, key);
            return Promise.resolve(scenario.looksOutcome ?? accepted(operation('r-looks', LOOKS)));
          },
          watch: (_slug: string, id: string) => of<AiWatchOperationOutcome>({ status: 'found', operation: operation(id, LOOKS) }),
        },
      },
      {
        provide: ImagePromptService,
        useValue: {
          request: (slug: string, body: unknown, key: string) => {
            note('prompt', slug, body, key);
            return Promise.resolve(accepted(operation('r-prompt', PROMPT)));
          },
          watch: (_slug: string, id: string) => of<AiWatchOperationOutcome>({ status: 'found', operation: operation(id, PROMPT) }),
        },
      },
      {
        provide: GeneratedImageService,
        useValue: {
          request: (slug: string, body: unknown, key: string) => {
            note('picture', slug, body, key);
            return Promise.resolve({ status: 'accepted', operation: imageRun('Requested') });
          },
          watch: (slug: string, id: string) => {
            note('picture.watch', slug, id);
            return of(scenario.imageWatch ?? { status: 'found', operation: imageRun('Succeeded') });
          },
          preview: () => of({ status: 'unavailable' }),
        },
      },
      {
        provide: DamAssetService,
        useValue: {
          keepGeneratedImage: (slug: string, body: unknown, key: string) => {
            note('keep', slug, body, key);
            return Promise.resolve(scenario.keep ?? { status: 'saved', asset: { id: 'asset-1' } });
          },
        },
      },
      {
        provide: RecipeService,
        useValue: {
          getRecipeDetail: (slug: string, id: string) => {
            note('read', slug, id);
            return Promise.resolve({
              status: 'found',
              recipe: { concurrencyToken: 'token-1', assetLinks: scenario.heroAlready ? [{ role: 'Hero' }] : [] },
            });
          },
        },
      },
      {
        provide: RecipeAssetLinkService,
        useValue: {
          link: (slug: string, recipeId: string, body: unknown) => {
            note('link', slug, recipeId, body);
            return Promise.resolve(scenario.link ?? { status: 'linked', recipe: {}, replayed: false });
          },
        },
      },
    ],
  }).compileComponents();

  harness = await RouterTestingHarness.create();
  await harness.navigateByUrl(scenario.url ?? `/${SLUG}/workflows/quick-recipe`);
  await settle();
}

const root = (): HTMLElement => harness.routeNativeElement as HTMLElement;
const text = (): string => (root().textContent ?? '').replace(/\s+/g, ' ');
const url = (): string => TestBed.inject(Router).url;

function control(label: string): HTMLElement | null {
  return (
    Array.from(root().querySelectorAll<HTMLElement>('button, a')).find((el) =>
      (el.textContent ?? '').trim().startsWith(label),
    ) ?? null
  );
}

async function press(label: string): Promise<void> {
  control(label)!.click();
  await settle();
}

async function nameAndStart(name = 'Miso butter corn'): Promise<void> {
  const input = root().querySelector<HTMLInputElement>('#cp-quick-recipe-name')!;
  input.value = name;
  input.dispatchEvent(new Event('input'));
  await press('Make my recipe');
}

describe('QuickRecipeComponent', () => {
  describe('naming the dish', () => {
    it('opens on the name, as step 1 of 3, with one heading and a way back to Workflows', async () => {
      await create();

      expect(root().querySelectorAll('h1').length).toBe(1);
      expect(text()).toContain('Step 1 of 3');
      expect(root().querySelector('#cp-quick-recipe-step-heading')?.textContent).toContain('Name your dish');
      expect(root().querySelector('a[cpBackLink]')?.getAttribute('href')).toBe(`/${SLUG}/workflows`);
      expect(calls).toEqual([]);
    });

    it('asks for nothing without a name, and says so beside the field', async () => {
      await create();

      await press('Make my recipe');

      expect(calls).toEqual([]);
      expect(root().querySelector('[role="alert"]')?.textContent).toContain('Say what the dish is called');
    });

    it('shows a Viewer no way to run it, and asks for nothing', async () => {
      await create({ role: 'Viewer' });

      expect(text()).toContain('view-only access');
      expect(root().querySelector('input')).toBeNull();
      expect(calls).toEqual([]);
    });

    it('answers an unknown workspace as not found', async () => {
      await create({ url: '/never-created/workflows/quick-recipe' });

      expect(text()).toContain("We couldn't find this workspace");
      expect(root().querySelector('input')).toBeNull();
    });
  });

  describe('making it', () => {
    it('asks for the draft by name, then the look, the brief for its main shot, and one picture', async () => {
      await create();

      await nameAndStart('  Miso butter corn ');

      expect(calls.filter((call) => !call.endsWith('.watch'))).toEqual(['draft', 'looks', 'prompt', 'picture']);
      expect(sent['draft'].slice(0, 2)).toEqual([SLUG, 'Miso butter corn']);
      // The look is planned from what the draft lists: a name where it gave one, the whole line where not.
      expect(sent['looks'][1]).toEqual(
        jasmine.objectContaining({
          dishName: 'Miso butter corn',
          creatorConcept:
            'Show the dish as its recipe makes it. Its ingredients: corn; 2 tbsp white miso. Add no food that is not one of them.',
        }),
      );
      // The main shot, though the look listed a detail shot first.
      expect(sent['prompt'][1]).toEqual(
        jasmine.objectContaining({ conceptRequestId: 'r-looks', conceptId: 'look-1', shotKind: 'Hero' }),
      );
      expect(sent['picture'][1]).toEqual({
        promptText: 'Charred corn, overhead.',
        avoidText: 'hands; text',
        variantCount: 1,
      });
    });

    it('keeps the run in the address, so a reload asks for nothing twice', async () => {
      await create();
      await nameAndStart();

      const params = TestBed.inject(Router).parseUrl(url()).queryParams;
      expect(params).toEqual({
        name: 'Miso butter corn',
        draft: 'r-draft',
        look: 'r-looks',
        prompt: 'r-prompt',
        picture: 'op-1',
      });

      await create({ url: url() });

      expect(calls).toEqual(['draft.watch', 'picture.watch']);
      expect(text()).toContain('Miso Butter Corn');
    });

    it('says the draft is on its way while it is, and offers no save', async () => {
      await create({
        draftOutcome: { status: 'accepted', operation: operation('r-draft', null, 'Requested'), replayed: false },
        draftWatch: () => NEVER,
      });

      await nameAndStart();

      expect(text()).toContain('Step 2 of 3');
      expect(text()).toContain('Making Miso butter corn');
      expect(control('Save')).toBeNull();
    });

    it('asks nothing of the picture until the draft has been written', async () => {
      await create({
        draftOutcome: { status: 'accepted', operation: operation('r-draft', null, 'Requested'), replayed: false },
        draftWatch: () => NEVER,
      });

      await nameAndStart();

      expect(calls.filter((call) => !call.endsWith('.watch'))).toEqual(['draft']);
      expect(text()).toContain('Starts once the recipe is drafted');
    });

    it('picks up a reloaded run whose draft is written and whose picture was never asked for', async () => {
      await create({ url: `/${SLUG}/workflows/quick-recipe?name=Miso%20butter%20corn&draft=r-draft` });

      expect(calls.filter((call) => !call.endsWith('.watch'))).toEqual(['looks', 'prompt', 'picture']);
    });

    it('asks for no picture for a draft that is already a recipe', async () => {
      await create({
        url: `/${SLUG}/workflows/quick-recipe?name=Miso%20butter%20corn&draft=r-draft`,
        draftWatch: () => of<AiWatchOperationOutcome>({ status: 'found', operation: operation('r-draft', DRAFT, 'Accepted') }),
      });

      expect(calls).toEqual(['draft.watch']);
      expect(text()).toContain('already been saved as a recipe');
    });

    it('does not spend a picture when the draft was refused', async () => {
      await create({ draftOutcome: { status: 'task_not_enabled' } });

      await nameAndStart();

      expect(calls).toEqual(['draft']);
      expect(text()).toContain('Step 1 of 3');
      expect(text()).toContain('switched off');
    });

    it('offers to try a failed draft again', async () => {
      await create({
        draftOutcome: { status: 'accepted', operation: operation('r-draft', null, 'Failed'), replayed: false },
      });

      await nameAndStart();

      expect(control('Try again')).not.toBeNull();
      expect(control('Save')).toBeNull();
      expect(calls).not.toContain('looks');
    });
  });

  describe('checking it', () => {
    it('shows the draft as generated, with its cautions, before anything is saved', async () => {
      await create();
      await nameAndStart();

      expect(text()).toContain('Step 3 of 3');
      expect(text()).toContain('Generated draft');
      expect(text()).toContain('Miso Butter Corn');
      expect(text()).toContain('4 ears corn');
      expect(text()).toContain('Char the corn.');
      expect(text()).toContain('Hot butter spits.');
      expect(text()).toContain('has not been checked for food safety');
      expect(calls).not.toContain('accept');
      expect(calls).not.toContain('keep');
    });

    it('says what the picture was planned from, and that nobody has checked it', async () => {
      await create();
      await nameAndStart();

      expect(root().querySelector('cp-content-pipeline-staged-image')).not.toBeNull();
      expect(text()).toContain("Planned from this draft's ingredients");
      expect(text()).toContain('Nothing has checked the picture against them');
    });

    it('opens the picture large, says only its size, and gives focus back on close', async () => {
      await create();
      await nameAndStart();
      document.body.appendChild(root());

      try {
        expect(root().querySelector('[role="dialog"]')).toBeNull();

        await press('View larger');

        const dialog = root().querySelector<HTMLElement>('[role="dialog"]')!;
        expect(dialog.querySelector('h2')?.textContent).toContain('Lead picture');
        expect(dialog.textContent).toContain('1024 × 1024 pixels');
        expect(dialog.querySelector('cp-content-pipeline-staged-image')).not.toBeNull();

        dialog.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }));
        await settle();

        expect(root().querySelector('[role="dialog"]')).toBeNull();
        expect(document.activeElement).toBe(control('View larger'));
      } finally {
        root().remove();
      }
    });

    it('offers no larger view when there is no picture', async () => {
      await create({ imageWatch: { status: 'found', operation: imageRun('Failed', 'provider-not-configured') } });
      await nameAndStart();

      expect(control('View larger')).toBeNull();
    });

    it('makes a different picture from the same draft, asking once', async () => {
      await create();
      await nameAndStart();

      await press('Make a different picture');

      const asked = calls.filter((call) => !call.endsWith('.watch'));
      expect(asked).toEqual(['draft', 'looks', 'prompt', 'picture', 'looks', 'prompt', 'picture']);
    });

    it('offers the same draft for line-by-line review', async () => {
      await create();
      await nameAndStart();

      expect(control('Change it line by line first')?.getAttribute('href')).toBe(
        `/${SLUG}/ai-recipe-studio/draft?request=r-draft`,
      );
    });

    it('asks before starting again while the draft is unsaved, and keeps it when the answer is no', async () => {
      await create();
      await nameAndStart();

      // This page holds the only way back to an unsaved draft and its picture, so clearing it unasked loses
      // the creator's way back to work they can still see on screen.
      confirmAnswer = false;
      await press('Start again with another name');

      expect(confirmRequests.map((request) => request.title)).toEqual(['Start again with another name?']);
      expect(text()).toContain('Miso Butter Corn');
    });

    it('goes back to the name once starting again is agreed to', async () => {
      await create();
      await nameAndStart();

      confirmAnswer = true;
      await press('Start again with another name');

      expect(confirmRequests.length).toBe(1);
      expect(text()).not.toContain('Miso Butter Corn');
      expect(text()).toContain('Step 1 of 3');
    });

    it('still offers the recipe when no picture could be made, and says why', async () => {
      await create({ imageWatch: { status: 'found', operation: imageRun('Failed', 'provider-not-configured') } });
      await nameAndStart();

      expect(text()).toContain('Making pictures is not switched on');
      expect(text()).toContain('You can still save the recipe without one');
      expect(control('Save recipe and picture')).toBeNull();
      expect(control('Save recipe')).not.toBeNull();
    });

    it('still offers the recipe when planning the picture was refused', async () => {
      await create({ looksOutcome: { status: 'task_not_enabled' } });
      await nameAndStart();

      expect(calls).not.toContain('prompt');
      expect(text()).toContain('Planning pictures is switched off');
      expect(control('Save recipe')).not.toBeNull();
    });
  });

  describe('saving', () => {
    it('creates the recipe, keeps the picture, makes it the lead picture, and opens the recipe', async () => {
      await create();
      await nameAndStart();

      await press('Save recipe and picture');

      expect(calls.slice(-4)).toEqual(['accept', 'keep', 'read', 'link']);
      expect(sent['accept']).toEqual([SLUG, 'r-draft', { acceptedChangeIds: DRAFT_CHANGE_IDS, rewrites: [] }]);
      expect(sent['keep'][1]).toEqual({ generatedImageId: 'img-1', metadata: { title: 'Miso Butter Corn' } });
      expect(sent['link'].slice(0, 3)).toEqual([
        SLUG,
        'recipe-1',
        {
          mediaAssetId: 'asset-1',
          role: 'Hero',
          instructionStepId: null,
          versionNumber: null,
          caption: null,
          expectedConcurrencyToken: 'token-1',
        },
      ]);
      expect(url()).toBe(`/${SLUG}/recipes/recipe-1`);
    });

    it('saves the recipe alone when asked to', async () => {
      await create();
      await nameAndStart();

      await press('Save without the picture');

      expect(calls).toContain('accept');
      expect(calls).not.toContain('keep');
      expect(calls).not.toContain('link');
      expect(url()).toBe(`/${SLUG}/recipes/recipe-1`);
    });

    it('creates nothing, and says why, when the draft cannot be accepted', async () => {
      await create({ accept: { status: 'refused', message: 'A recipe needs a title.' } });
      await nameAndStart();

      await press('Save recipe and picture');

      expect(calls).not.toContain('keep');
      expect(root().querySelector('[role="alert"]')?.textContent).toContain('A recipe needs a title.');
      expect(url()).toContain('/workflows/quick-recipe');
    });

    it('says the recipe is saved when only the picture failed, and never creates it twice', async () => {
      await create({ keep: { status: 'unavailable' } });
      await nameAndStart();

      await press('Save recipe and picture');

      expect(text()).toContain('Your recipe is saved.');
      expect(text()).toContain('could not be put in your library');
      expect(control('Open the recipe')?.getAttribute('href')).toBe(`/${SLUG}/recipes/recipe-1`);

      await press('Try adding the picture again');

      expect(calls.filter((call) => call === 'accept').length).toBe(1);
      expect(calls.filter((call) => call === 'keep').length).toBe(2);
    });

    it('leaves a lead picture that is already there alone', async () => {
      await create({ heroAlready: true });
      await nameAndStart();

      await press('Save recipe and picture');

      expect(calls).not.toContain('link');
      expect(url()).toBe(`/${SLUG}/recipes/recipe-1`);
    });
  });

  describe('two workspaces', () => {
    it("drops one workspace's run on the way to the other, and asks the other for nothing", async () => {
      await create();
      await nameAndStart();
      const before = calls.length;

      await harness.navigateByUrl(`/${OTHER_SLUG}/workflows/quick-recipe`);
      await settle();

      expect(text()).toContain('Step 1 of 3');
      expect(text()).not.toContain('Miso Butter Corn');
      expect(root().querySelector<HTMLInputElement>('#cp-quick-recipe-name')?.value).toBe('');
      expect(calls.length).toBe(before);
    });

    it('sends every request to the workspace in the address', async () => {
      await create({ url: `/${OTHER_SLUG}/workflows/quick-recipe` });
      await nameAndStart();
      await press('Save recipe and picture');

      for (const name of ['draft', 'looks', 'prompt', 'picture', 'accept', 'keep', 'read', 'link']) {
        expect(sent[name][0]).withContext(name).toBe(OTHER_SLUG);
      }
    });
  });
});
