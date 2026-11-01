import { Location } from '@angular/common';
import { Component, inject } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';

import { RecipeConceptStudioComponent } from './recipe-concept-studio.component';
import { RecipeFirstDraftReviewComponent } from './recipe-first-draft-review.component';
import { RecipeStudioFlowComponent } from './recipe-studio-flow.component';
import { recipeStudioFlowCanDeactivateGuard } from './recipe-studio-flow.guard';
import { RecipeStudioHost } from './recipe-studio-host';

// The two screens the flow hosts have specs of their own. Here they are stand-ins that read the host the way the
// real ones do and call it back, so this file covers only what the flow owns: its steps, its address, and how it
// answers each callback.

/** The ideas screen, as the flow sees it: the host it read, and the callbacks it can make. */
@Component({
  selector: 'cp-recipe-concept-studio',
  standalone: true,
  template: `<p class="ideas">{{ host.part() }}</p>`,
})
class IdeasStub {
  readonly host = inject(RecipeStudioHost);
  readonly conceptRequestId = this.host.conceptRequestId;
}

@Component({
  selector: 'cp-recipe-first-draft-review',
  standalone: true,
  template: `<p class="review">{{ draftRequestId }}</p>`,
  providers: [{ provide: RecipeFirstDraftReviewComponent, useExisting: ReviewStub }],
})
class ReviewStub {
  readonly host = inject(RecipeStudioHost);
  readonly draftRequestId = this.host.draftRequestId;
  /** What the creator answers when asked whether to lose their rewrites. */
  static leaveAnswer = true;
  static asked = 0;

  confirmDiscardIfDirty(): Promise<boolean> {
    ReviewStub.asked += 1;
    return Promise.resolve(ReviewStub.leaveAnswer);
  }
}

@Component({ standalone: true, template: '<p class="recipe">Recipe</p>' })
class RecipePage {}

const SLUG = 'cozy-fall';
const routeData = { title: 'AI Recipe Studio' };

let harness: RouterTestingHarness;

async function open(url: string): Promise<void> {
  TestBed.resetTestingModule();
  TestBed.configureTestingModule({
    providers: [
      provideRouter([
        {
          path: ':workspaceSlug/ai-recipe-studio',
          component: RecipeStudioFlowComponent,
          data: routeData,
          canDeactivate: [recipeStudioFlowCanDeactivateGuard],
        },
        {
          path: ':workspaceSlug/ai-recipe-studio/context/:contextId',
          component: RecipeStudioFlowComponent,
          data: routeData,
        },
        {
          path: ':workspaceSlug/ai-recipe-studio/draft',
          component: RecipeStudioFlowComponent,
          data: { ...routeData, legacyDraft: true },
        },
        { path: ':workspaceSlug/recipes/:recipeId', component: RecipePage },
        { path: ':workspaceSlug/elsewhere', component: RecipePage },
      ]),
    ],
  });
  TestBed.overrideComponent(RecipeStudioFlowComponent, {
    remove: { imports: [RecipeConceptStudioComponent, RecipeFirstDraftReviewComponent] },
    add: { imports: [IdeasStub, ReviewStub] },
  });
  harness = await RouterTestingHarness.create();
  await harness.navigateByUrl(url);
  await settle();
}

async function settle(): Promise<void> {
  await harness.fixture.whenStable();
  harness.detectChanges();
}

function root(): HTMLElement {
  return harness.routeNativeElement as HTMLElement;
}

function host(): RecipeStudioHost {
  return harness.routeDebugElement!.injector.get(RecipeStudioHost);
}

function heading(): string {
  return root().querySelector('#cp-recipe-studio-step-heading')?.textContent?.trim() ?? '';
}

function path(): string {
  return TestBed.inject(Location).path();
}

function button(label: string): HTMLButtonElement | null {
  return (
    Array.from(root().querySelectorAll<HTMLButtonElement>('[cpTopicCardActions] button')).find(
      (each) => each.textContent?.trim() === label,
    ) ?? null
  );
}

async function press(label: string): Promise<void> {
  button(label)!.click();
  await settle();
  await settle();
}

describe('RecipeStudioFlowComponent', () => {
  beforeEach(() => {
    ReviewStub.leaveAnswer = true;
    ReviewStub.asked = 0;
  });

  it('opens on the brief, as a flow of three steps under the studio’s own head', async () => {
    await open(`/${SLUG}/ai-recipe-studio`);

    expect(root().querySelector('cp-page-head cp-shell-nav-icon[cpPageHeadIcon]')).not.toBeNull();
    expect(heading()).toBe('Describe what you’re after');
    expect(root().querySelector('cp-progress')?.textContent).toContain('Step 1 of 3');
    const rows = Array.from(root().querySelectorAll('cp-stepper li')).map((row) => row.textContent ?? '');
    expect(rows.length).toBe(3);
    expect(rows[1]).toContain('Choose a concept');
    expect(rows[2]).toContain('Review the first draft');

    expect(host().part()).toBe('brief');
    expect(host().showMaker).withContext('the studio’s own page names the model').toBeTrue();
    expect(host().workspaceSlug).toBe(SLUG);
    // Nothing asked for yet, so nothing to continue to: asking is the step's own action.
    expect(button('Back')).toBeNull();
    expect(button('Continue')).toBeNull();
  });

  it('moves to the concepts once ideas are asked for, says so in the address, and takes focus there', async () => {
    await open(`/${SLUG}/ai-recipe-studio`);

    host().conceptsRequested('op-ideas');
    await settle();

    expect(heading()).toBe('Choose a concept');
    expect(host().part()).toBe('concepts');
    expect(root().querySelector('.ideas')?.textContent).toBe('concepts');
    expect(path()).toContain('step=concepts');
    expect(path()).toContain('request=op-ideas');
    expect(document.activeElement?.id).toBe('cp-recipe-studio-step-heading');
    // Focus reads the step's name; its place in the flow is said in the page's polite region.
    expect(root().querySelector('p.cp-sr-only[role="status"]')?.textContent?.trim()).toBe(
      'Step 2 of 3: Choose a concept.',
    );
  });

  it('keeps one ideas screen across the first two steps, so Back finds the brief as it was left', async () => {
    await open(`/${SLUG}/ai-recipe-studio`);
    const screen = root().querySelector('cp-recipe-concept-studio');

    host().conceptsRequested('op-ideas');
    await settle();
    await press('Back');

    expect(heading()).toBe('Describe what you’re after');
    expect(root().querySelector('cp-recipe-concept-studio')).toBe(screen);
    // Something was asked for, so Continue goes back to it.
    await press('Continue');
    expect(heading()).toBe('Choose a concept');
  });

  it('opens an address with ideas and no step on the concepts, and one with a draft on the review', async () => {
    await open(`/${SLUG}/ai-recipe-studio?request=op-ideas`);
    expect(heading()).toBe('Choose a concept');
    expect(host().conceptRequestId).toBe('op-ideas');

    await open(`/${SLUG}/ai-recipe-studio?request=op-ideas&draft=op-draft`);
    expect(heading()).toBe('Review the first draft');
    expect(root().querySelector('.review')?.textContent).toBe('op-draft');
  });

  it('moves to the review when a draft is asked for, and back to the concepts when it is closed', async () => {
    await open(`/${SLUG}/ai-recipe-studio?request=op-ideas`);

    host().draftRequested('op-draft');
    await settle();
    expect(heading()).toBe('Review the first draft');
    expect(path()).toContain('draft=op-draft');

    host().draftClosed();
    await settle();
    expect(heading()).toBe('Choose a concept');
    expect(path()).not.toContain('draft=');
    expect(host().draftRequestId).toBeNull();
  });

  it('asks before going back from the review, and stays when the creator keeps their rewrites', async () => {
    await open(`/${SLUG}/ai-recipe-studio?request=op-ideas&draft=op-draft`);
    ReviewStub.leaveAnswer = false;

    await press('Back');

    expect(ReviewStub.asked).toBe(1);
    expect(heading()).toBe('Review the first draft');
  });

  it('asks before leaving the page from the review, through the route guard', async () => {
    await open(`/${SLUG}/ai-recipe-studio?request=op-ideas&draft=op-draft`);
    ReviewStub.leaveAnswer = false;

    await TestBed.inject(Router).navigateByUrl(`/${SLUG}/elsewhere`);
    await settle();

    expect(ReviewStub.asked).toBe(1);
    expect(heading()).toBe('Review the first draft');
  });

  it('opens the new recipe once the draft is kept', async () => {
    await open(`/${SLUG}/ai-recipe-studio?request=op-ideas&draft=op-draft`);

    host().accepted('r-1');
    await settle();

    expect(path()).toBe(`/${SLUG}/recipes/r-1`);
  });

  it('starts over on the brief when a new brief is asked for, letting go of both requests', async () => {
    await open(`/${SLUG}/ai-recipe-studio?request=op-ideas`);

    host().briefRestarted();
    await settle();

    expect(heading()).toBe('Describe what you’re after');
    expect(path()).not.toContain('request=');
    expect(button('Continue')).toBeNull();
  });

  it('opens an old review address on the review, under the studio’s own address', async () => {
    await open(`/${SLUG}/ai-recipe-studio/draft?request=op-draft`);

    expect(heading()).toBe('Review the first draft');
    expect(root().querySelector('.review')?.textContent).toBe('op-draft');
    expect(path()).toBe(`/${SLUG}/ai-recipe-studio?step=draft&draft=op-draft`);
  });

  it('hands the piece of work it was opened for to the ideas screen, and keeps it in the address', async () => {
    await open(`/${SLUG}/ai-recipe-studio/context/ctx-1`);

    expect(host().contextId).toBe('ctx-1');
    host().conceptsRequested('op-ideas');
    await settle();
    expect(path()).toContain(`/${SLUG}/ai-recipe-studio/context/ctx-1?`);
  });
});
