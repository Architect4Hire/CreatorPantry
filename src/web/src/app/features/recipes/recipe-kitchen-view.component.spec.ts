import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Title } from '@angular/platform-browser';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';

import { PrintService } from '../../core/print.service';
import { ScreenWakeService } from '../../core/screen-wake.service';
import { MeasurementUnit } from '../../models/reference.models';
import { RecipeDetail } from '../../models/recipe.models';
import { RecipeDetailOutcome, RecipeService } from '../../services/recipe.service';
import { ListUnitsOutcome, ReferenceService } from '../../services/reference.service';
import { RecipeKitchenViewComponent, formatKitchenMinutes } from './recipe-kitchen-view.component';

const FAHRENHEIT: MeasurementUnit = {
  id: 'u-f',
  code: 'fahrenheit',
  displayName: 'degree Fahrenheit',
  pluralName: 'degrees Fahrenheit',
  abbreviation: '°F',
  dimension: 'Temperature',
  system: 'UsCustomary',
  baseUnitFactor: null,
  displayPrecision: 0,
};

const EMPTY_RECIPE: RecipeDetail = {
  id: 'r1',
  title: 'Skillet Cornbread',
  description: null,
  headnote: null,
  notes: null,
  storageNotes: null,
  attributionText: null,
  sourceUrl: null,
  cuisineId: null,
  courseId: null,
  primaryTechniqueId: null,
  prepTimeMinutes: null,
  cookTimeMinutes: null,
  restTimeMinutes: null,
  totalTimeMinutes: null,
  yieldText: null,
  yieldQuantity: null,
  yieldUnitId: null,
  servingCount: null,
  servingSize: null,
  status: 'Draft',
  duplicatedFrom: null,
  createdAt: '2026-01-01T00:00:00Z',
  updatedAt: '2026-01-02T00:00:00Z',
  concurrencyToken: 'AAAAAAAAB9E=',
  currentVersion: null,
  ingredientGroups: [],
  instructionGroups: [],
  equipment: [],
  assetLinks: [],
  tags: [],
};

function ingredient(id: string, sortOrder: number, displayText: string, isOptional = false): RecipeDetail['ingredientGroups'][number]['ingredients'][number] {
  return {
    id,
    sortOrder,
    displayText,
    displayTextSource: 'Creator',
    ingredientNameText: null,
    unitText: null,
    // Deliberately at odds with the line's wording: the view must show the words, not rebuild them.
    quantity: 99,
    quantityUpper: null,
    measurementUnitId: null,
    ingredientId: null,
    matchStatus: 'NotAttempted',
    preparationNote: null,
    isOptional,
    scalingBehavior: 'Proportional',
  };
}

function step(
  id: string,
  sortOrder: number,
  text: string,
  extra: Partial<RecipeDetail['instructionGroups'][number]['steps'][number]> = {},
): RecipeDetail['instructionGroups'][number]['steps'][number] {
  return { id, sortOrder, text, techniqueId: null, durationMinutes: null, temperatureValue: null, temperatureUnitId: null, note: null, ...extra };
}

const CORNBREAD: RecipeDetail = {
  ...EMPTY_RECIPE,
  description: 'Crisp at the edge, tender in the middle.',
  yieldText: 'One 10-inch skillet',
  servingCount: 8,
  prepTimeMinutes: 15,
  cookTimeMinutes: 25,
  // Not the sum of the two above, on purpose: the stored total is what is shown.
  totalTimeMinutes: 90,
  notes: 'Best the day it is made.',
  attributionText: 'Adapted from my grandmother.',
  ingredientGroups: [
    {
      id: 'g-wet',
      title: 'Wet',
      sortOrder: 2,
      ingredients: [ingredient('i-3', 1, '1 cup buttermilk, shaken')],
    },
    {
      id: 'g-dry',
      title: 'Dry',
      sortOrder: 1,
      ingredients: [ingredient('i-2', 2, 'a pinch of cayenne', true), ingredient('i-1', 1, '1 1/2 cups stone-ground cornmeal')],
    },
  ],
  instructionGroups: [
    {
      id: 's-a',
      title: 'Prepare',
      sortOrder: 1,
      steps: [step('st-1', 1, 'Heat the oven with the skillet in it.', { temperatureValue: 425, temperatureUnitId: 'u-f' })],
    },
    {
      id: 's-b',
      title: 'Bake',
      sortOrder: 2,
      steps: [
        step('st-2', 1, 'Pour the batter into the hot skillet.', { note: 'It should sizzle.' }),
        step('st-3', 2, 'Bake until golden.', { durationMinutes: 80 }),
      ],
    },
  ],
  equipment: [{ id: 'e-1', sortOrder: 1, displayText: '10-inch cast-iron skillet', equipmentTypeId: null, isOptional: false, note: null }],
};

class StubRecipeService {
  outcomes: RecipeDetailOutcome[] = [{ status: 'found', recipe: CORNBREAD }];
  calls: { readonly workspaceSlug: string; readonly recipeId: string }[] = [];

  getRecipeDetail(workspaceSlug: string, recipeId: string): Promise<RecipeDetailOutcome> {
    this.calls.push({ workspaceSlug, recipeId });

    // The last outcome repeats, so a test only lists what changes between reads.
    return Promise.resolve(this.outcomes.length > 1 ? this.outcomes.shift()! : this.outcomes[0]);
  }
}

class StubReferenceService {
  outcome: ListUnitsOutcome = { status: 'found', units: [FAHRENHEIT] };

  listUnits(): Promise<ListUnitsOutcome> {
    return Promise.resolve(this.outcome);
  }
}

class StubPrintService {
  prints = 0;

  print(): void {
    this.prints += 1;
  }
}

class StubScreenWakeService {
  isSupported = true;
  holds = 0;
  releases = 0;
  readonly refused = signal(false);
  readonly isRefused = this.refused.asReadonly();

  hold(): Promise<void> {
    this.holds += 1;

    return Promise.resolve();
  }

  release(): void {
    this.releases += 1;
  }
}

describe('RecipeKitchenViewComponent', () => {
  let recipes: StubRecipeService;
  let reference: StubReferenceService;
  let printer: StubPrintService;
  let screenWake: StubScreenWakeService;
  let harness: RouterTestingHarness;

  beforeEach(() => {
    recipes = new StubRecipeService();
    reference = new StubReferenceService();
    printer = new StubPrintService();
    screenWake = new StubScreenWakeService();

    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          {
            path: ':workspaceSlug',
            children: [{ path: 'recipes/:recipeId/kitchen', component: RecipeKitchenViewComponent }],
          },
        ]),
        { provide: RecipeService, useValue: recipes },
        { provide: ReferenceService, useValue: reference },
        { provide: PrintService, useValue: printer },
        { provide: ScreenWakeService, useValue: screenWake },
      ],
    });
  });

  async function open(url = '/cozy-fall/recipes/r1/kitchen'): Promise<HTMLElement> {
    harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(url, RecipeKitchenViewComponent);
    await settle();

    return harness.routeNativeElement!;
  }

  async function settle(): Promise<void> {
    await harness.fixture.whenStable();
    harness.detectChanges();
  }

  function texts(root: HTMLElement, selector: string): string[] {
    return Array.from(root.querySelectorAll(selector)).map((node) => node.textContent?.replace(/\s+/g, ' ').trim() ?? '');
  }

  function buttonWith(root: HTMLElement, text: string): HTMLButtonElement | undefined {
    return Array.from(root.querySelectorAll('button')).find((button) => button.textContent?.includes(text));
  }

  function checkboxLabelled(root: HTMLElement, label: string): HTMLInputElement {
    const match = Array.from(root.querySelectorAll('label')).find((node) => node.textContent?.trim() === label);
    expect(match).withContext(`a checkbox labelled "${label}"`).toBeTruthy();

    return match!.querySelector('input')!;
  }

  it('reads the recipe named by the route, in the workspace named by the route', async () => {
    await open('/cozy-fall/recipes/r1/kitchen');

    expect(recipes.calls).toEqual([{ workspaceSlug: 'cozy-fall', recipeId: 'r1' }]);
  });

  it('shows the title, what it makes and the stored times', async () => {
    const root = await open();

    expect(root.querySelector('h1')?.textContent).toBe('Skillet Cornbread');
    expect(texts(root, '.fact dt')).toEqual(['Makes', 'Serves', 'Prep', 'Cook', 'Total']);
    // The last is the stored total, not 15 + 25.
    expect(texts(root, '.fact dd')).toEqual(['One 10-inch skillet', '8', '15 min', '25 min', '1 hr 30 min']);
  });

  it("shows each ingredient line in the creator's own words, in the saved order", async () => {
    const root = await open();

    expect(texts(root, '.ingredients h3')).toEqual(['Dry', 'Wet']);
    expect(texts(root, '.ingredient label')).toEqual([
      '1 1/2 cups stone-ground cornmeal',
      'a pinch of cayenne (optional)',
      '1 cup buttermilk, shaken',
    ]);
    expect(root.querySelector('.ingredients')?.textContent).not.toContain('99');
  });

  it('numbers the steps through every section and shows each step\'s own time, temperature and note', async () => {
    const root = await open();

    expect(texts(root, '.method h3')).toEqual(['Prepare', 'Bake']);
    expect(texts(root, '.step-number')).toEqual(['1', '2', '3']);
    expect(texts(root, '.step-text')).toEqual([
      'Step 1. Heat the oven with the skillet in it.',
      'Step 2. Pour the batter into the hot skillet.',
      'Step 3. Bake until golden.',
    ]);
    expect(texts(root, '.step-facts')).toEqual(['425°F', '1 hr 20 min']);
    expect(texts(root, '.step-note')).toEqual(['It should sizzle.']);
    expect(root.querySelector('cp-notice.temperature-notice')).toBeNull();
  });

  it('shows the equipment, the notes and the attribution', async () => {
    const root = await open();

    expect(texts(root, '.plain-list li')).toEqual(['10-inch cast-iron skillet']);
    expect(root.querySelector('.notes')?.textContent).toContain('Best the day it is made.');
    expect(root.querySelector('.source')?.textContent).toContain('Adapted from my grandmother.');
  });

  it('leaves a temperature out, and says so, when the units cannot be read', async () => {
    reference.outcome = { status: 'unavailable' };

    const root = await open();

    expect(root.textContent).not.toContain('425');
    expect(root.querySelector('cp-notice.temperature-notice')?.textContent).toContain("Some step temperatures couldn't be shown");
    // The rest of the recipe is still there to cook from.
    expect(texts(root, '.step-number')).toEqual(['1', '2', '3']);
  });

  it('leaves a temperature out, and says so, when its unit is not one the catalogue returned', async () => {
    reference.outcome = { status: 'found', units: [] };

    const root = await open();

    expect(root.textContent).not.toContain('425');
    expect(root.querySelector('cp-notice.temperature-notice')).not.toBeNull();
  });

  it('ticks an ingredient off, and clears it again', async () => {
    const root = await open();
    expect(buttonWith(root, 'Clear ticks')).toBeUndefined();

    checkboxLabelled(root, '1 cup buttermilk, shaken').click();
    await settle();

    expect(texts(root, '.ingredient.ticked label .text')).toEqual(['1 cup buttermilk, shaken']);

    buttonWith(root, 'Clear ticks')!.click();
    await settle();

    expect(root.querySelector('.ingredient.ticked')).toBeNull();
    expect(checkboxLabelled(root, '1 cup buttermilk, shaken').checked).toBeFalse();
  });

  it('gives a step no box of its own: the method is read in order', async () => {
    const root = await open();

    expect(root.querySelector('.method cp-checkbox')).toBeNull();
    // The ingredients keep theirs: a shopping-and-measuring list is the part read out of order.
    expect(root.querySelectorAll('.ingredient cp-checkbox').length).toBe(3);
  });

  it('never writes: ticking reads nothing again and sends nothing', async () => {
    const root = await open();

    checkboxLabelled(root, '1 cup buttermilk, shaken').click();
    await settle();

    expect(recipes.calls.length).toBe(1);
  });

  it('renders a paper twin of every ingredient line, with the box left for a pen', async () => {
    const root = await open();

    expect(texts(root, '.ingredient .print-line')).toEqual([
      '1 1/2 cups stone-ground cornmeal',
      'a pinch of cayenne (optional)',
      '1 cup buttermilk, shaken',
    ]);
    // Not on screen, so a screen reader does not meet every line twice.
    expect(getComputedStyle(root.querySelector('.print-line')!).display).toBe('none');
  });

  it('keeps the screen on when asked, and hands it back when the tick is taken off', async () => {
    const root = await open();
    expect(screenWake.holds).toBe(0);

    // The same node throughout: a checked box carries its tick glyph, so it is no longer found by its words.
    const box = checkboxLabelled(root, 'Keep screen awake');
    expect(box.checked).toBeFalse();

    box.click();
    await settle();

    expect(screenWake.holds).toBe(1);
    expect(box.checked).toBeTrue();

    box.click();
    await settle();

    expect(screenWake.releases).toBe(1);
    expect(box.checked).toBeFalse();
  });

  it('offers nothing when the browser cannot keep the screen on', async () => {
    screenWake.isSupported = false;

    const root = await open();

    expect(root.textContent).not.toContain('Keep screen awake');
  });

  it('says the screen may still dim when the device turns the request down', async () => {
    const root = await open();
    const box = checkboxLabelled(root, 'Keep screen awake');

    // The region is here before it has anything to say, which is what makes the refusal announced.
    const notice = root.querySelector('cp-notice[role="status"]');
    expect(notice).withContext('a live region for the screen').toBeTruthy();
    expect(notice!.textContent?.trim()).toBe('');

    box.click();
    screenWake.refused.set(true);
    await settle();

    expect(notice!.textContent).toContain("This device wouldn't keep the screen on");
    // The tick stays: it is what was asked for, and the service goes on asking as the page is looked at again.
    expect(box.checked).toBeTrue();
  });

  it('hands the screen back on the way out', async () => {
    const root = await open();
    checkboxLabelled(root, 'Keep screen awake').click();
    await settle();

    harness.fixture.destroy();

    expect(screenWake.releases).toBe(1);
  });

  it('never writes: keeping the screen on reads nothing again and sends nothing', async () => {
    const root = await open();

    checkboxLabelled(root, 'Keep screen awake').click();
    await settle();

    expect(recipes.calls.length).toBe(1);
  });

  it('opens the print dialog from the button', async () => {
    const root = await open();
    expect(printer.prints).toBe(0);

    buttonWith(root, 'Print or save as PDF')!.click();

    expect(printer.prints).toBe(1);
  });

  it('names the tab after the recipe, which is the file name a saved PDF is offered', async () => {
    await open();

    expect(TestBed.inject(Title).getTitle()).toBe('Skillet Cornbread · CreatorPantry');
  });

  it('prints once on arrival when asked to, and takes the request off the address', async () => {
    await open('/cozy-fall/recipes/r1/kitchen?print=1');
    await settle();

    expect(printer.prints).toBe(1);
    expect(TestBed.inject(Router).url).toBe('/cozy-fall/recipes/r1/kitchen');
  });

  it('does not print on arrival for a recipe it could not show', async () => {
    recipes.outcomes = [{ status: 'not_found' }];

    await open('/cozy-fall/recipes/r1/kitchen?print=1');

    expect(printer.prints).toBe(0);
  });

  it('says a recipe is not there, with a way back to the list', async () => {
    recipes.outcomes = [{ status: 'not_found' }];

    const root = await open();

    expect(root.textContent).toContain('Recipe not found');
    expect(root.querySelector('a')?.getAttribute('href')).toBe('/cozy-fall/recipes');
  });

  it('offers a retry when the recipe cannot be read, and shows it once it can', async () => {
    recipes.outcomes = [{ status: 'unavailable' }, { status: 'found', recipe: CORNBREAD }];

    const root = await open();
    expect(root.textContent).toContain("Couldn't load this recipe");

    buttonWith(root, 'Try again')!.click();
    await settle();

    expect(root.querySelector('h1')?.textContent).toBe('Skillet Cornbread');
  });

  it('says there is nothing to cook from yet, and does not offer to print it', async () => {
    recipes.outcomes = [{ status: 'found', recipe: EMPTY_RECIPE }];

    const root = await open();

    expect(root.textContent).toContain('Nothing to cook from yet');
    expect(buttonWith(root, 'Print or save as PDF')!.disabled).toBeTrue();
    expect(Array.from(root.querySelectorAll('a')).at(-1)?.getAttribute('href')).toBe('/cozy-fall/recipes/r1');
  });

  it('links back to the recipe it came from', async () => {
    const root = await open();

    expect(root.querySelector('nav.back a')?.getAttribute('href')).toBe('/cozy-fall/recipes/r1');
  });
});

describe('formatKitchenMinutes', () => {
  it('words minutes the way a cook says them', () => {
    expect(formatKitchenMinutes(0)).toBe('0 min');
    expect(formatKitchenMinutes(45)).toBe('45 min');
    expect(formatKitchenMinutes(60)).toBe('1 hr');
    expect(formatKitchenMinutes(80)).toBe('1 hr 20 min');
  });
});
