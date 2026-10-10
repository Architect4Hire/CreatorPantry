import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Observable, Subject, of } from 'rxjs';

import { ConfirmService } from '../../core/confirm.service';
import { AiProposalStatus, AiProposedChange } from '../../models/ai-proposal.models';
import { ContentPipelineDraft, emptyContentPipelineDraft } from '../../models/content-pipeline.models';
import { ContentSeed, ContentSeedQuery } from '../../models/content-seed.models';
import { LinkedRecipe } from '../../models/creative-context.models';
import { AiRequestOutcome, AiWatchOperationOutcome } from '../../services/ai-request';
import { BrandProfileService } from '../../services/brand-profile.service';
import { ContentSeedService, GenerateContentSeedOutcome } from '../../services/content-seed.service';
import { DishFacetService } from '../../services/dish-facet.service';
import {
  ListReferenceEntriesOutcome,
  ListTechniquesOutcome,
  ReferenceService,
} from '../../services/reference.service';
import { ContentPipelineIdeaStepComponent } from './content-pipeline-idea-step.component';

function seed(overrides: Partial<ContentSeed> = {}): ContentSeed {
  return {
    token: 'abc-123',
    cuisine: { key: 'thai', displayName: 'Thai', pinned: false, fromRecipe: false },
    dishType: { key: 'main-course', displayName: 'Main course', pinned: false, fromRecipe: false },
    method: { key: 'stir-fry', displayName: 'Stir-fry', pinned: false, fromRecipe: false, requiresSafetyCaution: false },
    photographyStyle: { key: 'overhead-flat-lay', displayName: 'Overhead flat lay', pinned: false, fromRecipe: false },
    channel: { key: 'instagram', displayName: 'Instagram', pinned: false, fromRecipe: false },
    day: { day: 'Wednesday', pinned: false, theme: null },
    occasion: { key: 'weeknight', displayName: 'Weeknight', pinned: false, fromRecipe: false },
    description: 'Develop a Thai main course using the stir-fry method.',
    recipe: null,
    subject: null,
    ...overrides,
  };
}

@Component({
  imports: [ContentPipelineIdeaStepComponent],
  template: `<cp-content-pipeline-idea-step
    workspaceSlug="cozy-fall"
    [draft]="draft()"
    [recipe]="recipe()"
    (changed)="apply($event)"
    (announced)="announcements.push($event)"
  />`,
})
class HostComponent {
  readonly draft = signal<ContentPipelineDraft>(emptyContentPipelineDraft());
  readonly recipe = signal<LinkedRecipe | null>(null);
  readonly emitted: ContentPipelineDraft[] = [];
  readonly announcements: string[] = [];

  apply(next: ContentPipelineDraft): void {
    this.emitted.push(next);
    this.draft.set(next);
  }
}

let fixture: ComponentFixture<HostComponent>;
let host: HostComponent;
let el: HTMLElement;
let queries: { slug: string; query: ContentSeedQuery }[];
let pending: Subject<GenerateContentSeedOutcome>[];
let confirmAnswer: boolean;
let confirmCalls: number;
/** False to make all three vocabulary reads fail, which is the only failure mode the step distinguishes. */
let vocabularyReadable: boolean;
let vocabularyReads: number;

const CUISINES: ListReferenceEntriesOutcome = {
  status: 'found',
  entries: [
    { id: 'c1', code: 'levantine', displayName: 'Levantine' },
    { id: 'c2', code: 'thai', displayName: 'Thai' },
  ],
};

const COURSES: ListReferenceEntriesOutcome = {
  status: 'found',
  entries: [
    { id: 'k1', code: 'salad', displayName: 'Salad' },
    { id: 'k2', code: 'main-course', displayName: 'Main course' },
  ],
};

const TECHNIQUES: ListTechniquesOutcome = {
  status: 'found',
  techniques: [
    { id: 't1', code: 'grill', displayName: 'Grill', requiresSafetyCaution: false },
    { id: 't2', code: 'stir-fry', displayName: 'Stir-fry', requiresSafetyCaution: false },
  ],
};

const referenceFake = {
  listCuisines: (): Promise<ListReferenceEntriesOutcome> => {
    vocabularyReads += 1;
    return Promise.resolve(vocabularyReadable ? CUISINES : { status: 'unavailable' });
  },
  listCourses: (): Promise<ListReferenceEntriesOutcome> =>
    Promise.resolve(vocabularyReadable ? COURSES : { status: 'unavailable' }),
  listTechniques: (): Promise<ListTechniquesOutcome> =>
    Promise.resolve(vocabularyReadable ? TECHNIQUES : { status: 'unavailable' }),
  listPhotographyStyles: () =>
    Promise.resolve(
      rowCataloguesReadable
        ? {
            status: 'found',
            entries: [
              { key: 'overhead-flat-lay', displayName: 'Overhead flat lay', isActive: true },
              { key: 'dark-and-moody', displayName: 'Dark and moody', isActive: true },
              { key: 'retired-look', displayName: 'Retired look', isActive: false },
            ],
          }
        : { status: 'unavailable' },
    ),
  listOccasions: () =>
    Promise.resolve(
      rowCataloguesReadable
        ? {
            status: 'found',
            entries: [
              { key: 'weeknight', displayName: 'Weeknight', isActive: true },
              { key: 'picnic-and-outdoors', displayName: 'Picnic and outdoors', isActive: true },
            ],
          }
        : { status: 'unavailable' },
    ),
};

/** False to leave the idea's own rows without the three lists only they use. */
let rowCataloguesReadable: boolean;

const brandProfileFake = {
  listContentChannels: () =>
    Promise.resolve(
      rowCataloguesReadable
        ? {
            status: 'found',
            channels: [
              { key: 'instagram', displayName: 'Instagram', isActive: true },
              { key: 'newsletter', displayName: 'Newsletter', isActive: true },
            ],
          }
        : { status: 'unavailable' },
    ),
};

function rowPicker(name: string): HTMLSelectElement | null {
  return el.querySelector(`#cp-pipeline-facet-${name}`);
}

/** Change one part of the idea on screen the way a creator does. */
async function changePart(name: string, value: string): Promise<void> {
  const control = rowPicker(name)!;
  control.value = value;
  control.dispatchEvent(new Event('change'));
  await settle();
}

let nameRequests: { slug: string; dishName: string; key: string }[];
let nameWatches: string[];
/** What asking for a reading answers. Switched off by default, so a test that is not about it sees none. */
let nameOutcome: AiRequestOutcome;

function facetRow(fieldName: string, afterValue: string): AiProposedChange {
  return {
    changeId: `c-${fieldName}`,
    changeKind: 'Set',
    targetKind: 'DishFacetSuggestion',
    targetId: 't1',
    fieldName,
    beforeValue: null,
    afterValue,
    proposedPosition: null,
    disposition: 'Pending',
  };
}

/** A finished reading: a Levantine salad, with the method declined. */
function readOperation(requestId = 'r-1'): AiProposalStatus {
  return {
    aiProposalRequestId: requestId,
    status: 'Proposed',
    taskType: 'DishFacetSuggestion',
    scope: 'NotApplicable',
    sourceVersionId: null,
    requestedAt: '2026-10-10T12:00:00Z',
    statusChangedAt: '2026-10-10T12:00:00Z',
    failureCategory: null,
    proposal: {
      proposalId: 'p1',
      outputSchemaVersion: '1',
      promptTemplateId: 'recipe.dish-facets',
      promptTemplateVersion: '1.0.0',
      promptTemplateBodyChecksum: 'abc',
      providerName: 'provider',
      modelName: 'model',
      createdAt: '2026-10-10T12:00:00Z',
      changes: [
        facetRow('facet.Cuisine', 'levantine'),
        facetRow('facet.Cuisine.confidence', 'Likely'),
        facetRow('facet.Cuisine.rationale', 'Fattoush is a Levantine dish.'),
        facetRow('facet.DishType', 'salad'),
        facetRow('facet.DishType.confidence', 'Possible'),
        facetRow('facet.DishType.rationale', 'The name says salad.'),
        facetRow('facet.Method.rationale', 'The name does not say how it is cooked.'),
      ],
      warnings: [],
    },
  };
}

const dishFacetFake = {
  request: (slug: string, dishName: string, key: string): Promise<AiRequestOutcome> => {
    nameRequests.push({ slug, dishName, key });
    return Promise.resolve(nameOutcome);
  },
  watch: (_slug: string, requestId: string): Observable<AiWatchOperationOutcome> => {
    nameWatches.push(requestId);
    return of<AiWatchOperationOutcome>({ status: 'found', operation: readOperation(requestId) });
  },
};

function select(facet: 'cuisine' | 'dishType' | 'method'): HTMLSelectElement | null {
  return el.querySelector(`#cp-pipeline-stated-${facet}`);
}

/** Choose an option in one of the three selects the way a creator does, and let the step settle. */
async function choose(facet: 'cuisine' | 'dishType' | 'method', code: string): Promise<void> {
  const control = select(facet)!;
  control.value = code;
  control.dispatchEvent(new Event('change'));
  await settle();
}

function generate(slug: string, query: ContentSeedQuery): Observable<GenerateContentSeedOutcome> {
  queries.push({ slug, query });
  const subject = new Subject<GenerateContentSeedOutcome>();
  pending.push(subject);
  return subject.asObservable();
}

async function settle(): Promise<void> {
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
}

async function mount(draft: ContentPipelineDraft = emptyContentPipelineDraft()): Promise<void> {
  fixture = TestBed.createComponent(HostComponent);
  host = fixture.componentInstance;
  host.draft.set(draft);
  el = fixture.nativeElement;
  await settle();
}

async function answer(outcome: GenerateContentSeedOutcome, index = pending.length - 1): Promise<void> {
  pending[index].next(outcome);
  pending[index].complete();
  await settle();
}

function buttonWith(text: string): HTMLButtonElement {
  return Array.from(el.querySelectorAll('button')).find((button) =>
    (button.textContent ?? '').trim().startsWith(text),
  ) as HTMLButtonElement;
}

async function click(text: string): Promise<void> {
  buttonWith(text).click();
  await settle();
}

const latest = (): ContentPipelineDraft => host.emitted[host.emitted.length - 1];

describe('ContentPipelineIdeaStepComponent', () => {
  beforeEach(() => {
    queries = [];
    pending = [];
    confirmAnswer = true;
    confirmCalls = 0;
    vocabularyReadable = true;
    vocabularyReads = 0;
    nameRequests = [];
    nameWatches = [];
    nameOutcome = { status: 'task_not_enabled' };
    rowCataloguesReadable = true;

    TestBed.configureTestingModule({
      providers: [
        { provide: ContentSeedService, useValue: { generate } },
        { provide: DishFacetService, useValue: dishFacetFake },
        { provide: BrandProfileService, useValue: brandProfileFake },
        { provide: ReferenceService, useValue: referenceFake },
        {
          provide: ConfirmService,
          useValue: {
            confirm: () => {
              confirmCalls += 1;
              return Promise.resolve(confirmAnswer);
            },
          },
        },
      ],
    });
  });

  it('asks for nothing until the creator does, because an idea is a suggestion', async () => {
    await mount();

    expect(queries.length).toBe(0);
    expect(el.textContent).toContain('No idea yet');
    expect(buttonWith('Suggest an idea')).toBeTruthy();
  });

  it('shows an idea and keeps only the code that reproduces it, never the idea itself', async () => {
    await mount();
    await click('Suggest an idea');

    expect(el.textContent).toContain('Putting an idea together');

    await answer({ status: 'found', seed: seed() });

    expect(el.textContent).toContain('Develop a Thai main course using the stir-fry method.');
    expect(latest().seed.lastToken).toBe('abc-123');
    expect(latest().seed.accepted).toBeNull();
    expect(host.announcements).toContain('A new idea is ready.');
  });

  it('comes back to the same idea after a reload, from the code the draft kept', async () => {
    const base = emptyContentPipelineDraft();
    await mount({ ...base, seed: { lastToken: 'abc-123', keep: {}, accepted: null } });

    expect(queries.length).toBe(1);
    expect(queries[0].query.token).toBe('abc-123');
    expect(queries[0].slug).toBe('cozy-fall');
  });

  it('needs nothing fetched for an idea that was already picked', async () => {
    const base = emptyContentPipelineDraft();
    await mount({ ...base, seed: { lastToken: 'abc-123', keep: {}, accepted: seed() } });

    expect(queries.length).toBe(0);
    expect(el.textContent).toContain('This is the idea you picked.');
  });

  it('holds onto a part of the idea and sends it as a pin on the next try', async () => {
    await mount();
    await click('Suggest an idea');
    await answer({ status: 'found', seed: seed() });

    const keep = Array.from(el.querySelectorAll<HTMLButtonElement>('button[aria-pressed]')).find((button) =>
      (button.getAttribute('aria-label') ?? '').startsWith('Keep cuisine'),
    )!;
    keep.click();
    await settle();

    expect(latest().seed.keep.cuisine).toBe('thai');
    expect(keep.getAttribute('aria-pressed')).toBe('true');

    await click('Try another');
    expect(queries[queries.length - 1].query.cuisine).toBe('thai');
    expect(queries[queries.length - 1].query.token).toBeNull();
  });

  it('writes a kept channel and day back to the setup step, so neither value has two homes', async () => {
    await mount();
    await click('Suggest an idea');
    await answer({ status: 'found', seed: seed() });

    const keepChannel = Array.from(el.querySelectorAll<HTMLButtonElement>('button[aria-pressed]')).find((button) =>
      (button.getAttribute('aria-label') ?? '').startsWith('Keep channel'),
    )!;
    keepChannel.click();
    await settle();

    expect(latest().config.channelKey).toBe('instagram');
    expect(latest().seed.keep).toEqual({});

    const keepDay = Array.from(el.querySelectorAll<HTMLButtonElement>('button[aria-pressed]')).find((button) =>
      (button.getAttribute('aria-label') ?? '').startsWith('Keep day'),
    )!;
    keepDay.click();
    await settle();

    expect(latest().config.day).toBe('Wednesday');
  });

  it('cancels the idea in flight when a second is asked for, so the first cannot land on top', async () => {
    await mount();
    await click('Suggest an idea');
    expect(pending[0].observed).toBeTrue();

    // The code box is on screen throughout, so a second ask really can arrive while the first is in flight.
    const token = el.querySelector<HTMLInputElement>('#cp-pipeline-token')!;
    token.value = 'spring-bakes';
    token.dispatchEvent(new Event('input'));
    await settle();
    await click('Show that idea');

    expect(pending.length).toBe(2);
    expect(pending[0].observed).toBeFalse();

    await answer({ status: 'found', seed: seed({ token: 'second', description: 'The second idea.' }) }, 1);
    expect(el.textContent).toContain('The second idea.');

    // The abandoned answer arriving late changes nothing.
    pending[0].next({ status: 'found', seed: seed({ description: 'The first idea.' }) });
    await settle();
    expect(el.textContent).not.toContain('The first idea.');
  });

  it('shows a caution when the method carries one', async () => {
    await mount();
    await click('Suggest an idea');
    await answer({
      status: 'found',
      seed: seed({
        method: { key: 'pressure-canning', displayName: 'Pressure canning', pinned: false, fromRecipe: false, requiresSafetyCaution: true },
      }),
    });

    expect(el.textContent).toContain('This method needs care');
  });

  it('says nothing at all about safety when no caution is attached, and never that a method is safe', async () => {
    await mount();
    await click('Suggest an idea');
    await answer({ status: 'found', seed: seed() });

    expect(el.textContent).not.toContain('This method needs care');
    expect(el.textContent?.toLowerCase()).not.toContain('is safe');
  });

  it('keeps the caution on screen after the idea is picked', async () => {
    await mount();
    await click('Suggest an idea');
    await answer({
      status: 'found',
      seed: seed({
        method: { key: 'pressure-canning', displayName: 'Pressure canning', pinned: false, fromRecipe: false, requiresSafetyCaution: true },
      }),
    });
    await click('Pick this idea');

    expect(latest().seed.accepted?.method?.requiresSafetyCaution).toBeTrue();
    expect(el.textContent).toContain('This method needs care');
  });

  it('shows the parts the creator asked for as theirs', async () => {
    await mount();
    await click('Suggest an idea');
    await answer({
      status: 'found',
      seed: seed({ channel: { key: 'instagram', displayName: 'Instagram', pinned: true, fromRecipe: false } }),
    });

    expect(el.textContent).toContain('You asked for this');
  });

  it("shows the workspace's own theme for the day, and does not offer to keep it", async () => {
    await mount();
    await click('Suggest an idea');
    await answer({
      status: 'found',
      seed: seed({
        day: { day: 'Wednesday', pinned: false, theme: { key: 'midweek', displayName: 'Midweek meals', pinned: false, fromRecipe: false } },
      }),
    });

    expect(el.textContent).toContain('Midweek meals');
    const labels = Array.from(el.querySelectorAll('button[aria-pressed]')).map((b) => b.getAttribute('aria-label'));
    expect(labels.some((label) => (label ?? '').includes('theme'))).toBeFalse();
  });

  it('picks an idea, and lets the creator go back to looking without losing it', async () => {
    await mount();
    await click('Suggest an idea');
    await answer({ status: 'found', seed: seed() });
    await click('Pick this idea');

    expect(latest().seed.accepted?.token).toBe('abc-123');
    expect(host.announcements).toContain('Idea picked.');

    await click('Change the idea');

    expect(latest().seed.accepted).toBeNull();
    // Nothing was re-fetched: the idea is still on screen as a suggestion.
    expect(queries.length).toBe(1);
    expect(el.textContent).toContain('Develop a Thai main course');
  });

  describe('choosing what to work from', () => {
    const IDEA = 'Develop a Thai main course using the stir-fry method.';
    const MINE = 'A tight crop of the first slice.';

    function choices(): HTMLInputElement[] {
      return Array.from(el.querySelectorAll<HTMLInputElement>('#cp-pipeline-brief-choice input[type="radio"]'));
    }

    async function choose(index: number): Promise<void> {
      choices()[index].click();
      await settle();
    }

    async function picked(concept: string): Promise<void> {
      const base = emptyContentPipelineDraft();
      await mount({ ...base, config: { ...base.config, concept } });
      await click('Suggest an idea');
      await answer({ status: 'found', seed: seed() });
      await click('Pick this idea');
    }

    it('offers no way to put the idea in place of the description', async () => {
      await picked(MINE);

      expect(buttonWith('Use this wording as my description')).toBeFalsy();
    });

    it('records the idea as the brief when there is no description to choose against, and asks nothing', async () => {
      await picked('');

      expect(choices().length).toBe(0);
      expect(latest().config.briefSource).toBe('Idea');
      expect(latest().config.brief).toBe(IDEA);
      expect(latest().config.concept).withContext('the description is not filled in for them').toBe('');
    });

    it('asks once there is a description and a picked idea, with nothing chosen for the creator', async () => {
      await picked(MINE);

      expect(choices().length).toBe(3);
      expect(choices().some((choice) => choice.checked)).toBeFalse();
      expect(latest().config.briefSource).toBeNull();
      expect(latest().config.brief).toBe('');
    });

    it('shows, on each choice, exactly the brief it would make', async () => {
      await picked(MINE);

      const section = el.querySelector('#cp-pipeline-brief-choice')!.textContent ?? '';
      expect(section).toContain('Work from my description');
      expect(section).toContain('Work from this idea');
      expect(section).toContain('Combine them');
      expect(section).toContain(MINE);
      expect(section).toContain(IDEA);
    });

    it('works from the description as it was written', async () => {
      await picked(MINE);
      await choose(0);

      expect(latest().config.briefSource).toBe('Description');
      expect(latest().config.brief).toBe(MINE);
    });

    it('works from the idea, and leaves the description exactly as it was', async () => {
      await picked(MINE);
      await choose(1);

      expect(latest().config.briefSource).toBe('Idea');
      expect(latest().config.brief).toBe(IDEA);
      expect(latest().config.concept).toBe(MINE);
    });

    it('combines them by putting the idea below the description, which is not rewritten', async () => {
      await picked(MINE);
      await choose(2);

      expect(latest().config.briefSource).toBe('Combined');
      expect(latest().config.brief).toBe(`${MINE}\n\n${IDEA}`);
      expect(latest().config.concept).toBe(MINE);
      expect(host.announcements).toContain('Combine them. The brief is set.');
    });

    it('asks before a new choice replaces a brief the creator edited, and keeps it on a no', async () => {
      await picked(MINE);
      await choose(0);
      host.draft.set({ ...host.draft(), config: { ...host.draft().config, brief: 'A tight crop, and steam.' } });
      await settle();
      expect(el.textContent).toContain('You have edited the brief');

      confirmAnswer = false;
      await choose(2);

      expect(confirmCalls).toBe(1);
      expect(host.draft().config.briefSource).toBe('Description');
      expect(host.draft().config.brief).toBe('A tight crop, and steam.');
      expect(choices().map((choice) => choice.checked))
        .withContext('the mark goes back to the choice that stands')
        .toEqual([true, false, false]);

      confirmAnswer = true;
      await choose(2);

      expect(host.draft().config.briefSource).toBe('Combined');
      expect(host.draft().config.brief).toBe(`${MINE}\n\n${IDEA}`);
    });

    it('leaves a brief that came with the run alone when an idea is picked', async () => {
      const base = emptyContentPipelineDraft();
      await mount({ ...base, config: { ...base.config, briefSource: 'Idea', brief: 'Chosen on another device.' } });
      await click('Suggest an idea');
      await answer({ status: 'found', seed: seed() });
      await click('Pick this idea');

      expect(latest().config.brief).toBe('Chosen on another device.');
    });
  });

  it('shows a refused pin, keeps it, and offers to let it go', async () => {
    const base = emptyContentPipelineDraft();
    await mount({ ...base, seed: { lastToken: null, keep: { cuisine: 'atlantean' }, accepted: null } });
    await click('Suggest an idea');
    await answer({
      status: 'refused',
      fieldErrors: { Cuisine: ['That cuisine is no longer available.'] },
    });

    expect(el.textContent).toContain('That cuisine is no longer available.');
    expect(el.textContent).toContain('Nothing you chose has been changed');
    // Still kept: nothing was silently dropped to make the request succeed.
    expect(host.draft().seed.keep.cuisine).toBe('atlantean');

    await click('Stop keeping cuisine');

    expect(latest().seed.keep.cuisine).toBeUndefined();
  });

  it('shows a refused pin under the name the server gives the field', async () => {
    // The request binds `Cuisine`; the problem document that comes back names it `cuisine`.
    const base = emptyContentPipelineDraft();
    await mount({ ...base, seed: { lastToken: null, keep: { cuisine: 'atlantean' }, accepted: null } });
    await click('Suggest an idea');
    await answer({ status: 'refused', fieldErrors: { cuisine: ['That cuisine is no longer available.'] } });

    expect(el.textContent).toContain('That cuisine is no longer available.');
    expect(buttonWith('Stop keeping cuisine')).toBeTruthy();
  });

  describe('with a recipe linked to the run', () => {
    const LINKED: LinkedRecipe = { recipeId: 'recipe-1', recipeVersionId: 'version-1' };

    const aroundRecipe = (overrides: Partial<ContentSeed> = {}): ContentSeed =>
      seed({
        cuisine: { key: 'french', displayName: 'French', pinned: false, fromRecipe: true },
        dishType: { key: 'dessert', displayName: 'Dessert', pinned: false, fromRecipe: true },
        method: { key: 'bake', displayName: 'Bake', pinned: false, fromRecipe: true, requiresSafetyCaution: false },
        description: 'Plan a post about "Lemon Tart", a French dessert made using the bake method.',
        recipe: { ...LINKED, title: 'Lemon Tart' },
        ...overrides,
      });

    async function mountLinked(draft: ContentPipelineDraft = emptyContentPipelineDraft()): Promise<void> {
      fixture = TestBed.createComponent(HostComponent);
      host = fixture.componentInstance;
      host.draft.set(draft);
      host.recipe.set(LINKED);
      el = fixture.nativeElement;
      await settle();
    }

    it('asks for an idea around that recipe, at the version that was linked', async () => {
      await mountLinked();

      expect(el.textContent).toContain('around the recipe you linked');

      await click('Suggest an idea');

      expect(queries[0].query.recipeId).toBe('recipe-1');
      expect(queries[0].query.recipeVersionId).toBe('version-1');
    });

    it("shows the recipe's own parts as the recipe's, and does not offer to keep them", async () => {
      await mountLinked();
      await click('Suggest an idea');
      await answer({ status: 'found', seed: aroundRecipe() });

      expect(el.textContent).toContain('Plan a post about "Lemon Tart"');
      expect(el.textContent).toContain('Built around your recipe, Lemon Tart.');
      expect(el.querySelectorAll('cp-badge').length).toBe(3);
      expect(el.textContent).toContain('From your recipe');

      const keepable = Array.from(el.querySelectorAll('button[aria-pressed]')).map(
        (button) => button.getAttribute('aria-label') ?? '',
      );
      expect(keepable.some((label) => /cuisine|dish type|method/i.test(label))).toBeFalse();
      // What the recipe does not decide is still the creator's to keep.
      expect(keepable.some((label) => /photo style/i.test(label))).toBeTrue();
    });

    it('asks again for a suggestion when the link changes, with the same code', async () => {
      await mount();
      await click('Suggest an idea');
      await answer({ status: 'found', seed: seed() });
      expect(queries.length).toBe(1);

      host.recipe.set(LINKED);
      await settle();

      expect(queries.length).toBe(2);
      expect(queries[1].query.token).toBe('abc-123');
      expect(queries[1].query.recipeId).toBe('recipe-1');

      // And an answer built around it settles: nothing is asked a third time.
      await answer({ status: 'found', seed: aroundRecipe() });
      expect(queries.length).toBe(2);
    });

    it('leaves a picked idea alone, says it is out of step, and asks again only when told to', async () => {
      const base = emptyContentPipelineDraft();
      await mountLinked({ ...base, seed: { lastToken: 'abc-123', keep: {}, accepted: seed() } });

      // A decision is not replaced for the creator.
      expect(queries.length).toBe(0);
      expect(el.textContent).toContain('You picked this idea before linking a recipe');

      await click('Suggest one for the recipe');

      expect(host.draft().seed.accepted).toBeNull();
      expect(queries.length).toBe(1);
      expect(queries[0].query.token).toBeNull();
      expect(queries[0].query.recipeId).toBe('recipe-1');
    });

    it('says a picked idea was built around a recipe that is no longer the one linked', async () => {
      const base = emptyContentPipelineDraft();
      const other = aroundRecipe({ recipe: { recipeId: 'recipe-1', recipeVersionId: 'version-0', title: 'Lemon Tart' } });
      await mountLinked({ ...base, seed: { lastToken: 'abc-123', keep: {}, accepted: other } });

      expect(el.textContent).toContain('an earlier version of the one linked now');

      host.recipe.set(null);
      await settle();

      expect(el.textContent).toContain('a recipe that is no longer linked');
      expect(buttonWith('Suggest a new idea')).toBeTruthy();
    });

    it('says nothing is out of step for an idea picked around the recipe linked now', async () => {
      const base = emptyContentPipelineDraft();
      await mountLinked({ ...base, seed: { lastToken: 'abc-123', keep: {}, accepted: aroundRecipe() } });

      expect(el.querySelector('cp-notice')).toBeNull();
    });

    it('says so when the linked recipe cannot be read, rather than showing nothing', async () => {
      await mountLinked();
      await click('Suggest an idea');
      await answer({ status: 'refused', fieldErrors: { recipeId: ['That recipe could not be found.'] } });

      expect(el.textContent).toContain("couldn't be read, so no idea was made");
      expect(el.textContent).not.toContain('Some of what you are keeping');

      await click('Try again');
      expect(queries.length).toBe(2);
    });
  });

  it('reports an outage as an outage, with a way to try again', async () => {
    await mount();
    await click('Suggest an idea');
    await answer({ status: 'unavailable' });

    expect(el.textContent).toContain('could not be put together');
    await click('Try again');
    expect(queries.length).toBe(2);
  });

  it('reproduces an idea from a code the creator pasted, and refuses one that is not a code', async () => {
    await mount();

    const token = el.querySelector<HTMLInputElement>('#cp-pipeline-token')!;
    token.value = 'not a code';
    token.dispatchEvent(new Event('input'));
    await settle();

    expect(el.textContent).toContain('A code is up to 64 letters');
    expect(buttonWith('Show that idea').disabled).toBeTrue();

    token.value = 'spring-bakes';
    token.dispatchEvent(new Event('input'));
    await settle();

    await click('Show that idea');
    expect(queries[queries.length - 1].query.token).toBe('spring-bakes');
  });

  it("shows the idea's own code so it can be kept or shared", async () => {
    await mount();
    await click('Suggest an idea');
    await answer({ status: 'found', seed: seed() });

    expect(el.querySelector('code')?.textContent).toBe('abc-123');
  });

  // The gap this closes: each facet is drawn independently, so reaching a particular cuisine, dish type and
  // method by re-rolling meant re-rolling past every combination that was not those three.
  describe('stating a facet the creator already knows', () => {
    it('offers the three vocabularies, with suggesting one as the default', async () => {
      await mount();

      expect(select('cuisine')!.value).toBe('');
      expect(Array.from(select('cuisine')!.options).map((option) => option.value)).toEqual([
        '',
        'levantine',
        'thai',
      ]);
      expect(Array.from(select('dishType')!.options).map((option) => option.value)).toEqual([
        '',
        'salad',
        'main-course',
      ]);
      expect(Array.from(select('method')!.options).map((option) => option.value)).toEqual([
        '',
        'grill',
        'stir-fry',
      ]);
    });

    // Before anything has been suggested, which is the whole point: the first idea already honours it.
    it('asks for the stated facet on the very first idea', async () => {
      await mount();

      await choose('cuisine', 'levantine');
      await choose('dishType', 'salad');
      await choose('method', 'grill');
      expect(queries.length).toBe(0);

      await click('Suggest an idea');

      expect(queries[0].query.cuisine).toBe('levantine');
      expect(queries[0].query.dishType).toBe('salad');
      expect(queries[0].query.method).toBe('grill');
    });

    // The same code, so the facet that was stated changes and the rest of the idea stays where it was. A fresh
    // code would re-roll the day, the shot and the occasion along with it.
    it('re-asks under the same code when a facet is stated after an idea is on screen', async () => {
      await mount();
      await click('Suggest an idea');
      await answer({ status: 'found', seed: seed() });

      await choose('cuisine', 'levantine');

      expect(queries.length).toBe(2);
      expect(queries[1].query.token).toBe('abc-123');
      expect(queries[1].query.cuisine).toBe('levantine');
    });

    // Nothing to disagree with, so the idea on screen stands rather than being thrown away.
    it('does not re-ask when a facet is released', async () => {
      await mount();
      await click('Suggest an idea');
      await answer({ status: 'found', seed: seed() });
      await choose('cuisine', 'levantine');
      await answer({ status: 'found', seed: seed({ cuisine: { key: 'levantine', displayName: 'Levantine', pinned: true, fromRecipe: false } }) });

      const before = queries.length;
      await choose('cuisine', '');

      expect(queries.length).toBe(before);
      expect(latest().seed.keep.cuisine).toBeUndefined();
    });

    // Pressing Keep on a facet the idea already shows asks for exactly the idea already on screen.
    it('does not re-ask when Keep is pressed on what is already shown', async () => {
      await mount();
      await click('Suggest an idea');
      await answer({ status: 'found', seed: seed() });

      await click('Keep');

      expect(queries.length).toBe(1);
    });

    // One record of what the creator asked for, so the select and the Keep button cannot disagree.
    it('shows a facet kept from an idea as the stated one', async () => {
      await mount();
      await click('Suggest an idea');
      await answer({ status: 'found', seed: seed() });

      await click('Keep');
      expect(latest().seed.keep.cuisine).toBe('thai');
      expect(select('cuisine')!.value).toBe('thai');
    });

    // A linked recipe's own cuisine, course and technique win over a pin server-side, so a picker beside them
    // would be a control the next request ignores.
    it('is not offered once a recipe is linked, or once an idea is picked', async () => {
      await mount();
      expect(select('cuisine')).not.toBeNull();

      host.recipe.set({ recipeId: 'r1', recipeVersionId: null });
      await settle();
      expect(select('cuisine')).toBeNull();

      host.recipe.set(null);
      await settle();
      await click('Suggest an idea');
      await answer({ status: 'found', seed: seed() });
      await click('Pick this idea');

      expect(select('cuisine')).toBeNull();
    });

    it('disables the selects and says so when the vocabulary cannot be read, leaving the rest working', async () => {
      vocabularyReadable = false;
      await mount();

      expect(select('cuisine')!.disabled).toBeTrue();
      expect(el.textContent).toContain('cooking vocabulary could not be loaded');

      // The step still works without it.
      await click('Suggest an idea');
      expect(queries.length).toBe(1);

      vocabularyReadable = true;
      await click('Load the vocabulary again');

      expect(vocabularyReads).toBe(2);
      expect(select('cuisine')!.disabled).toBeFalse();
    });
  });

  describe('changing one part of the idea', () => {
    const shown = async (): Promise<void> => {
      await mount();
      await click('Suggest an idea');
      await answer({ status: 'found', seed: seed() });
    };

    it('offers every part its own picker, showing what it is set to, and none for the theme', async () => {
      await mount();
      await click('Suggest an idea');
      await answer({
        status: 'found',
        seed: seed({ day: { day: 'Wednesday', pinned: false, theme: { key: 'soup', displayName: 'Soup day', pinned: false, fromRecipe: false } } }),
      });

      for (const [name, value] of [
        ['cuisine', 'thai'],
        ['dishType', 'main-course'],
        ['method', 'stir-fry'],
        ['photographyStyle', 'overhead-flat-lay'],
        ['channel', 'instagram'],
        ['occasion', 'weeknight'],
        ['day', 'Wednesday'],
      ]) {
        expect(rowPicker(name)?.value).withContext(name).toBe(value);
      }
      expect(rowPicker('theme')).toBeNull();
      // A retired style is never offered as a new choice.
      expect(rowPicker('photographyStyle')!.textContent).not.toContain('Retired look');
    });

    it('asks for the same idea again with only the changed part moved', async () => {
      await shown();

      await changePart('photographyStyle', 'dark-and-moody');

      expect(latest().seed.keep).toEqual({ photographyStyle: 'dark-and-moody' });
      expect(queries.length).toBe(2);
      expect(queries[1].query.token).toBe('abc-123');
      expect(queries[1].query.photographyStyle).toBe('dark-and-moody');
      expect(queries[1].query.cuisine).toBeNull();
    });

    it('writes a changed channel and day where the first step keeps them', async () => {
      await shown();

      await changePart('channel', 'newsletter');
      await answer({
        status: 'found',
        seed: seed({ channel: { key: 'newsletter', displayName: 'Newsletter', pinned: true, fromRecipe: false } }),
      });
      await changePart('day', 'Friday');

      expect(latest().config.channelKey).toBe('newsletter');
      expect(latest().config.day).toBe('Friday');
      expect(latest().seed.keep).toEqual({});
      expect(queries[1].query.channel).toBe('newsletter');
      expect(queries[2].query.day).toBe('Friday');
      expect(queries[2].query.token).toBe('abc-123');
    });

    it('leaves a part as text with Keep when its list could not be read', async () => {
      rowCataloguesReadable = false;

      await shown();

      expect(rowPicker('occasion')).toBeNull();
      expect(rowPicker('cuisine')).not.toBeNull();
      expect(el.querySelector('.facets')!.textContent).toContain('Weeknight');
    });

    it('offers nothing to change once the idea is picked', async () => {
      await shown();
      await click('Pick this idea');

      expect(rowPicker('cuisine')).toBeNull();
      expect(el.querySelector('.facets')!.textContent).toContain('Thai');
    });

    it('does not offer a part that is the linked recipe’s own', async () => {
      await mount();
      await click('Suggest an idea');
      await answer({
        status: 'found',
        seed: seed({ cuisine: { key: 'thai', displayName: 'Thai', pinned: false, fromRecipe: true } }),
      });

      expect(rowPicker('cuisine')).toBeNull();
      expect(rowPicker('occasion')).not.toBeNull();
    });
  });

  describe('with a name typed instead of a recipe', () => {
    const NAMED = (): ContentPipelineDraft => {
      const base = emptyContentPipelineDraft();

      return { ...base, config: { ...base.config, subject: 'Fattoush salad with radishes' } };
    };

    const hintOf = (facet: 'cuisine' | 'dishType' | 'method'): string =>
      el.querySelector(`#cp-pipeline-stated-${facet}-hint`)?.textContent?.trim() ?? '';

    it('reads the name and fills the controls the creator has not answered', async () => {
      nameOutcome = { status: 'accepted', operation: readOperation(), replayed: false };

      await mount(NAMED());
      await settle();

      expect(nameRequests.length).toBe(1);
      expect(nameRequests[0].slug).toBe('cozy-fall');
      expect(nameRequests[0].dishName).toBe('Fattoush salad with radishes');
      expect(latest().seed.keep).toEqual({ cuisine: 'levantine', dishType: 'salad' });
      expect(latest().seed.nameReading).toEqual({
        requestId: 'r-1',
        subject: 'Fattoush salad with radishes',
        applied: true,
      });
      expect(select('cuisine')!.value).toBe('levantine');
      expect(select('method')!.value).toBe('');
      expect(host.announcements).toContain('Filled in 2 of 3 from the name. Change any that are wrong.');
    });

    it('says beside each control where its value came from, and why one was left alone', async () => {
      nameOutcome = { status: 'accepted', operation: readOperation(), replayed: false };

      await mount(NAMED());
      await settle();

      expect(hintOf('cuisine')).toContain('Suggested from the name');
      expect(hintOf('cuisine')).toContain('Fattoush is a Levantine dish.');
      expect(hintOf('dishType')).toContain('A guess from the name');
      expect(hintOf('method')).toContain('The name did not say.');

      await choose('cuisine', 'thai');

      expect(hintOf('cuisine')).toBe('');
    });

    it('never overwrites a facet the creator already stated', async () => {
      nameOutcome = { status: 'accepted', operation: readOperation(), replayed: false };
      const named = NAMED();

      await mount({ ...named, seed: { ...named.seed, keep: { cuisine: 'thai' } } });
      await settle();

      expect(latest().seed.keep).toEqual({ cuisine: 'thai', dishType: 'salad' });
      expect(hintOf('cuisine')).toBe('');
    });

    it('reads a kept reading back rather than asking again, and does not refill an emptied control', async () => {
      const named = NAMED();

      await mount({
        ...named,
        seed: {
          ...named.seed,
          keep: { dishType: 'salad' },
          nameReading: { requestId: 'r-7', subject: 'Fattoush salad with radishes', applied: true },
        },
      });
      await settle();

      expect(nameRequests.length).toBe(0);
      expect(nameWatches).toEqual(['r-7']);
      expect(select('cuisine')!.value).toBe('');
      expect(host.emitted.length).toBe(0);
      expect(hintOf('dishType')).toContain('A guess from the name');
    });

    it('says when the name could not be read, and retries under the same key', async () => {
      nameOutcome = { status: 'unavailable' };

      await mount(NAMED());
      await settle();

      expect(el.textContent).toContain('could not be read just now');
      expect(select('cuisine')!.disabled).toBeFalse();

      nameOutcome = { status: 'accepted', operation: readOperation(), replayed: false };
      await click('Read the name again');
      await settle();

      expect(nameRequests.length).toBe(2);
      expect(nameRequests[1].key).toBe(nameRequests[0].key);
      expect(select('cuisine')!.value).toBe('levantine');
      expect(el.textContent).not.toContain('could not be read just now');
    });

    it('says nothing at all when the reading is switched off', async () => {
      await mount(NAMED());
      await settle();

      expect(nameRequests.length).toBe(1);
      expect(el.textContent).not.toContain('could not be read');
      expect(host.emitted.length).toBe(0);
    });

    it('does not read a name while a recipe is linked', async () => {
      nameOutcome = { status: 'accepted', operation: readOperation(), replayed: false };
      fixture = TestBed.createComponent(HostComponent);
      host = fixture.componentInstance;
      host.draft.set(NAMED());
      host.recipe.set({ recipeId: 'rec-1', recipeVersionId: 'ver-1', title: 'Fattoush' } as LinkedRecipe);
      el = fixture.nativeElement;
      await settle();

      expect(nameRequests.length).toBe(0);
    });

    it('asks for an idea about the name the creator typed', async () => {
      await mount(NAMED());

      await click('Suggest an idea');

      expect(queries[0].query.subject).toBe('Fattoush salad with radishes');
      expect(queries[0].query.recipeId).toBeNull();
    });

    it('sends no name once a recipe is linked, because the route refuses both', async () => {
      await mount(NAMED());
      host.recipe.set({ recipeId: 'recipe-1', recipeVersionId: 'version-1' });
      await settle();

      await click('Suggest an idea');

      expect(queries[0].query.subject).toBeNull();
      expect(queries[0].query.recipeId).toBe('recipe-1');
    });

    it('says which name a suggestion was built around', async () => {
      await mount(NAMED());
      await click('Suggest an idea');
      await answer({ status: 'found', seed: seed({ subject: 'Fattoush salad with radishes' }) });

      expect(el.textContent).toContain('Built around what you called it, Fattoush salad with radishes.');
    });

    it('asks again for a suggestion when the name changes, because a suggestion costs nothing', async () => {
      await mount(NAMED());
      await click('Suggest an idea');
      await answer({ status: 'found', seed: seed({ token: 'abc-123', subject: 'Fattoush salad with radishes' }) });

      host.draft.set({ ...host.draft(), config: { ...host.draft().config, subject: 'Barmbrack' } });
      await settle();

      expect(queries.length).toBe(2);
      // The same code, so the creator sees the same idea rebuilt for the new name rather than a fresh roll.
      expect(queries[1].query.token).toBe('abc-123');
      expect(queries[1].query.subject).toBe('Barmbrack');
    });

    it('leaves a picked idea alone and says it is about the earlier name', async () => {
      await mount(NAMED());
      await click('Suggest an idea');
      await answer({ status: 'found', seed: seed({ subject: 'Fattoush salad with radishes' }) });
      await click('Pick this idea');

      const asked = queries.length;
      host.draft.set({ ...host.draft(), config: { ...host.draft().config, subject: 'Barmbrack' } });
      await settle();

      expect(el.textContent).toContain('renamed what this picture is of since picking this idea');
      // A picked idea is a decision: nothing replaces it without the creator asking.
      expect(queries.length).toBe(asked);
      expect(latest().seed.accepted?.subject).toBe('Fattoush salad with radishes');
    });

    it('says nothing about a name added after an idea was picked', async () => {
      await mount();
      await click('Suggest an idea');
      await answer({ status: 'found', seed: seed() });
      await click('Pick this idea');

      host.draft.set({ ...host.draft(), config: { ...host.draft().config, subject: 'Barmbrack' } });
      await settle();

      expect(el.textContent).not.toContain('renamed what this picture is of');
    });
  });
});
