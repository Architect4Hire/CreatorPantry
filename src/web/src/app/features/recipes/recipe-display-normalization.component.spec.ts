import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { RuntimeConfigService } from '../../core/runtime-config.service';
import { RecipeIngredientGroup } from '../../models/recipe.models';
import { RecipeDisplayNormalizationComponent } from './recipe-display-normalization.component';

const RECIPE_ID = '2a1b7c6d-0000-4000-8000-000000000001';
const DISPLAY_URL = `https://gateway.example/api/v1/workspaces/cozy-fall/recipes/${RECIPE_ID}/calculations/normalize-display`;
const UNITS_URL = 'https://gateway.example/api/v1/reference/units';

const CATALOGUE = [
  {
    id: 'unit-cup',
    code: 'cup-us',
    displayName: 'cup',
    pluralName: 'cups',
    abbreviation: 'c',
    dimension: 'Volume',
    system: 'UsCustomary',
    baseUnitFactor: 236.588,
    displayPrecision: 2,
  },
  {
    id: 'unit-gram',
    code: 'gram',
    displayName: 'gram',
    pluralName: 'grams',
    abbreviation: 'g',
    dimension: 'Mass',
    system: 'Metric',
    baseUnitFactor: 1,
    displayPrecision: 2,
  },
];

const SAVED_GROUPS: readonly RecipeIngredientGroup[] = [
  {
    id: 'group-1',
    title: null,
    sortOrder: 0,
    ingredients: [
      {
        id: 'line-1',
        sortOrder: 0,
        displayText: '1½ cups all-purpose flour',
        ingredientNameText: 'all-purpose flour',
        quantity: 1.5,
        quantityUpper: null,
        measurementUnitId: 'unit-cup',
        ingredientId: null,
        displayTextSource: 'Creator',
        unitText: null,
        matchStatus: 'NotAttempted',
        preparationNote: null,
        isOptional: false,
        scalingBehavior: 'Proportional',
      },
      {
        id: 'line-2',
        sortOrder: 1,
        displayText: 'salt, to taste',
        ingredientNameText: 'salt',
        quantity: null,
        quantityUpper: null,
        measurementUnitId: null,
        ingredientId: null,
        displayTextSource: 'Creator',
        unitText: null,
        matchStatus: 'NotAttempted',
        preparationNote: null,
        isOptional: false,
        scalingBehavior: 'ReviewRequired',
      },
    ],
  },
];

function payload(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    sourceVersionNumber: 3,
    result: {
      text: '1½ cups',
      presentation: 'Fraction',
      upperPresentation: null,
      precision: 2,
      rounding: 'ToEven',
      ...overrides,
    },
  };
}

interface Harness {
  readonly fixture: ComponentFixture<RecipeDisplayNormalizationComponent>;
  readonly http: HttpTestingController;
}

/** Lets the reads already answered land, without waiting on any still in flight. */
async function landed(fixture: ComponentFixture<RecipeDisplayNormalizationComponent>): Promise<void> {
  for (let i = 0; i < 6; i += 1) await Promise.resolve();
  fixture.detectChanges();
}

async function createFixture(options: { sourceVersionNumber?: number | null } = {}): Promise<Harness> {
  await TestBed.configureTestingModule({
    imports: [RecipeDisplayNormalizationComponent],
    providers: [provideHttpClient(), provideHttpClientTesting()],
  }).compileComponents();

  const http = TestBed.inject(HttpTestingController);
  const runtimeConfig = TestBed.inject(RuntimeConfigService);
  const loading = runtimeConfig.load();
  http.expectOne('/runtime-config.json').flush({ gatewayUrl: 'https://gateway.example' });
  await loading;

  const fixture = TestBed.createComponent(RecipeDisplayNormalizationComponent);
  fixture.componentRef.setInput('workspaceSlug', 'cozy-fall');
  fixture.componentRef.setInput('recipeId', RECIPE_ID);
  fixture.componentRef.setInput(
    'sourceVersionNumber',
    options.sourceVersionNumber === undefined ? 3 : options.sourceVersionNumber,
  );
  fixture.componentRef.setInput('savedIngredientGroups', SAVED_GROUPS);
  fixture.detectChanges();

  http.expectOne((request) => request.url === UNITS_URL).flush({ items: CATALOGUE, nextCursor: null });
  await landed(fixture);
  return { fixture, http };
}

function rowCells(fixture: ComponentFixture<RecipeDisplayNormalizationComponent>, index: number): string[] {
  const row = (fixture.nativeElement as HTMLElement).querySelectorAll('.display-lines tbody tr')[index];
  return Array.from(row.querySelectorAll('th, td')).map((cell) => (cell.textContent ?? '').trim());
}

function text(fixture: ComponentFixture<RecipeDisplayNormalizationComponent>): string {
  return ((fixture.nativeElement as HTMLElement).textContent ?? '').replace(/\s+/g, ' ');
}

describe('RecipeDisplayNormalizationComponent', () => {
  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('reads every line it can on its own — nothing to fill in — beside what the recipe records', async () => {
    const { fixture, http } = await createFixture();

    // Only the line with an amount and a unit is asked about, with the defaults.
    const read = http.expectOne(DISPLAY_URL);
    expect(read.request.body).toEqual(
      jasmine.objectContaining({
        sourceVersionNumber: 3,
        value: 1.5,
        unitId: 'unit-cup',
        precision: 2,
        useAbbreviation: false,
        rounding: 'ToEven',
      }),
    );
    read.flush(payload());
    await landed(fixture);

    expect(rowCells(fixture, 0)).toEqual(['1½ cups all-purpose flour', '1.5 cups', '1½ cups']);
    // A line that cannot be read says why, in words, rather than vanishing.
    expect(rowCells(fixture, 1)).toEqual(['salt, to taste', '—', 'No amount recorded']);
    // No form: no value, unit or button to fill in and press.
    expect(fixture.nativeElement.querySelector('input[type="number"]')).toBeNull();
    expect(text(fixture)).not.toContain('Show how it reads');
  });

  it('reads the recipe again with short units when asked, the one option in view', async () => {
    const { fixture, http } = await createFixture();
    http.expectOne(DISPLAY_URL).flush(payload());
    await landed(fixture);

    const shortUnits = fixture.nativeElement.querySelector('cp-checkbox input') as HTMLInputElement;
    shortUnits.click();
    fixture.detectChanges();

    const again = http.expectOne(DISPLAY_URL);
    expect(again.request.body).toEqual(jasmine.objectContaining({ useAbbreviation: true }));
    again.flush(payload({ text: '1½ c' }));
    await landed(fixture);
    expect(rowCells(fixture, 0)[2]).toBe('1½ c');
  });

  it('keeps decimal places and rounding folded away, and reads again when one changes', async () => {
    const { fixture, http } = await createFixture();
    http.expectOne(DISPLAY_URL).flush(payload());
    await landed(fixture);

    const fold = fixture.nativeElement.querySelector('details.display-more') as HTMLDetailsElement;
    expect(fold.open).toBeFalse();

    fixture.componentInstance.setPrecision('1');
    fixture.detectChanges();
    const again = http.expectOne(DISPLAY_URL);
    expect(again.request.body).toEqual(jasmine.objectContaining({ precision: 1 }));
    again.flush(payload());
    await landed(fixture);
  });

  it('says when nothing could be read, and reads again on asking', async () => {
    const { fixture, http } = await createFixture();
    http.expectOne(DISPLAY_URL).flush({}, { status: 503, statusText: 'Unavailable' });
    await landed(fixture);

    expect(text(fixture)).toContain('Your amounts couldn’t be read just now.');
    const tryAgain = Array.from(fixture.nativeElement.querySelectorAll('button') as NodeListOf<HTMLButtonElement>).find(
      (button) => button.textContent?.trim() === 'Try again',
    )!;
    tryAgain.click();
    fixture.detectChanges();

    http.expectOne(DISPLAY_URL).flush(payload());
    await landed(fixture);
    expect(rowCells(fixture, 0)[2]).toBe('1½ cups');
  });

  it('asks nothing of a recipe with no saved version, and says to save it first', async () => {
    const { fixture, http } = await createFixture({ sourceVersionNumber: null });

    http.expectNone(DISPLAY_URL);
    expect(text(fixture)).toContain('Save the recipe first');
  });
});
