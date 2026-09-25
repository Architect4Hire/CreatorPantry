import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { RuntimeConfigService } from '../../core/runtime-config.service';
import { RecipeIngredientGroup } from '../../models/recipe.models';
import { RecipeUnitConversionComponent } from './recipe-unit-conversion.component';

const RECIPE_ID = '2a1b7c6d-0000-4000-8000-000000000001';
const CONVERT_URL = `https://gateway.example/api/v1/workspaces/cozy-fall/recipes/${RECIPE_ID}/calculations/convert-units`;
const UNITS_URL = 'https://gateway.example/api/v1/reference/units';

function unit(id: string, code: string, dimension: string, abbreviation: string): Record<string, unknown> {
  return {
    id,
    code,
    displayName: code,
    pluralName: `${code}s`,
    abbreviation,
    dimension,
    system: 'Metric',
    baseUnitFactor: dimension === 'Temperature' ? null : 1,
    displayPrecision: 2,
  };
}

const CATALOGUE = [
  unit('unit-gram', 'gram', 'Mass', 'g'),
  unit('unit-kilogram', 'kilogram', 'Mass', 'kg'),
  unit('unit-cup', 'cup', 'Volume', 'cup'),
  unit('unit-celsius', 'celsius', 'Temperature', '°C'),
];

/** Saved lines covering all three cases: usable, no unit, no amount. */
const SAVED_GROUPS: readonly RecipeIngredientGroup[] = [
  {
    id: 'group-1',
    title: null,
    sortOrder: 0,
    ingredients: [
      {
        id: 'line-1',
        sortOrder: 0,
        displayText: '250 g all-purpose flour',
        ingredientNameText: 'all-purpose flour',
        quantity: 250,
        quantityUpper: null,
        measurementUnitId: 'unit-gram',
        ingredientId: null,
        matchStatus: 'NotAttempted',
        preparationNote: null,
        isOptional: false,
        scalingBehavior: 'Proportional',
      },
      {
        id: 'line-2',
        sortOrder: 1,
        displayText: '2 large eggs',
        ingredientNameText: 'eggs',
        quantity: 2,
        quantityUpper: null,
        measurementUnitId: null,
        ingredientId: null,
        matchStatus: 'NotAttempted',
        preparationNote: null,
        isOptional: false,
        scalingBehavior: 'ReviewRequired',
      },
      {
        id: 'line-3',
        sortOrder: 2,
        displayText: 'salt, to taste',
        ingredientNameText: 'salt',
        quantity: null,
        quantityUpper: null,
        measurementUnitId: null,
        ingredientId: null,
        matchStatus: 'NotAttempted',
        preparationNote: null,
        isOptional: false,
        scalingBehavior: 'ReviewRequired',
      },
    ],
  },
];

function conversionPayload(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    sourceVersionNumber: 3,
    result: {
      method: 'SameDimensionFactor',
      convertedQuantity: { numerator: '1', denominator: '4' },
      convertedDisplayQuantity: 0.25,
      precision: 2,
      rounding: 'ToEven',
      formula: '× (1 gram-per-base ÷ 1000 kilogram-per-base)',
      source: null,
      ...overrides,
    },
  };
}

interface Harness {
  readonly fixture: ComponentFixture<RecipeUnitConversionComponent>;
  readonly http: HttpTestingController;
}

async function createFixture(options: { catalogueFails?: boolean } = {}): Promise<Harness> {
  await TestBed.configureTestingModule({
    imports: [RecipeUnitConversionComponent],
    providers: [provideHttpClient(), provideHttpClientTesting()],
  }).compileComponents();

  const http = TestBed.inject(HttpTestingController);
  const runtimeConfig = TestBed.inject(RuntimeConfigService);
  const loading = runtimeConfig.load();
  http.expectOne('/runtime-config.json').flush({ gatewayUrl: 'https://gateway.example' });
  await loading;

  const fixture = TestBed.createComponent(RecipeUnitConversionComponent);
  fixture.componentRef.setInput('workspaceSlug', 'cozy-fall');
  fixture.componentRef.setInput('recipeId', RECIPE_ID);
  fixture.componentRef.setInput('sourceVersionNumber', 3);
  fixture.componentRef.setInput('savedIngredientGroups', SAVED_GROUPS);
  fixture.detectChanges();

  const catalogue = http.expectOne((request) => request.url === UNITS_URL);
  if (options.catalogueFails) {
    catalogue.flush({}, { status: 500, statusText: 'Server Error' });
  } else {
    catalogue.flush({ items: CATALOGUE, nextCursor: null });
  }

  await fixture.whenStable();
  fixture.detectChanges();

  return { fixture, http };
}

async function convert(
  harness: Harness,
  respond: (request: ReturnType<HttpTestingController['expectOne']>) => void,
): Promise<void> {
  const call = harness.fixture.componentInstance.convert();
  respond(harness.http.expectOne(CONVERT_URL));
  await call;
  harness.fixture.detectChanges();
}

function text(fixture: ComponentFixture<RecipeUnitConversionComponent>): string {
  return (fixture.nativeElement as HTMLElement).textContent ?? '';
}

function optionTexts(fixture: ComponentFixture<RecipeUnitConversionComponent>, selectId: string): string[] {
  const select: HTMLSelectElement = fixture.nativeElement.querySelector(`#${selectId}`);
  return Array.from(select.options).map((option) => option.textContent?.trim() ?? '');
}

describe('RecipeUnitConversionComponent', () => {
  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('says that nothing is saved before any conversion is made', async () => {
    const { fixture } = await createFixture();

    expect(text(fixture)).toContain('nothing here is saved');
    expect(text(fixture)).toContain('Choose an amount and two units');
  });

  // ---- Catalogue ----

  it('groups the unit pickers by dimension', async () => {
    const { fixture } = await createFixture();
    const select: HTMLSelectElement = fixture.nativeElement.querySelector(
      `#${fixture.componentInstance.fromFieldId}`,
    );
    const groups = Array.from(select.querySelectorAll('optgroup')).map((group) => group.label);

    expect(groups).toEqual(['Weight', 'Volume', 'Temperature']);
  });

  // The temperature section beside this one does not need the catalogue, so a failure here degrades one
  // section rather than the tab.
  it('degrades to a retry when the catalogue cannot be loaded', async () => {
    const { fixture, http } = await createFixture({ catalogueFails: true });

    expect(text(fixture)).toContain("unit catalogue couldn't be loaded");
    expect(fixture.nativeElement.querySelector('form')).toBeNull();

    const retry: HTMLButtonElement = fixture.nativeElement.querySelector('button');
    retry.click();
    http.expectOne((request) => request.url === UNITS_URL).flush({ items: CATALOGUE, nextCursor: null });
    await fixture.whenStable();
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('form')).not.toBeNull();
  });

  // ---- Unresolved values ----

  it('lists every saved line, disabling the ones that cannot fill the form and saying why', async () => {
    const { fixture } = await createFixture();
    const select: HTMLSelectElement = fixture.nativeElement.querySelector(
      `#${fixture.componentInstance.prefillFieldId}`,
    );

    const options = Array.from(select.options);
    expect(options.map((option) => option.textContent?.trim())).toEqual([
      'Choose a line…',
      '250 g all-purpose flour',
      '2 large eggs — no unit recorded',
      'salt, to taste — no amount recorded',
    ]);
    expect(options.map((option) => option.disabled)).toEqual([false, false, true, true]);
  });

  it('fills the amount and unit from a resolved line', async () => {
    const { fixture } = await createFixture();

    fixture.componentInstance.prefillFrom('line-1');
    fixture.detectChanges();

    expect(fixture.componentInstance.quantityValue()).toBe(250);
    expect(fixture.componentInstance.fromUnitId()).toBe('unit-gram');
  });

  it('fills nothing from an unresolved line', async () => {
    const { fixture } = await createFixture();

    fixture.componentInstance.prefillFrom('line-3');

    expect(fixture.componentInstance.quantityValue()).toBeNull();
    expect(fixture.componentInstance.fromUnitId()).toBe('');
  });

  // ---- Converting ----

  it('sends the amount, both units and the saved source version', async () => {
    const harness = await createFixture();
    harness.fixture.componentInstance.quantityValue.set(1000);
    harness.fixture.componentInstance.fromUnitId.set('unit-gram');
    harness.fixture.componentInstance.toUnitId.set('unit-kilogram');

    await convert(harness, (req) => {
      expect(req.request.body).toEqual({
        sourceVersionNumber: 3,
        quantity: 1000,
        fromUnitId: 'unit-gram',
        toUnitId: 'unit-kilogram',
      });
      req.flush(conversionPayload());
    });

    expect(text(harness.fixture)).toContain('0.25 kg');
  });

  it('shows the formula, precision, rounding and method', async () => {
    const harness = await createFixture();
    harness.fixture.componentInstance.quantityValue.set(1000);
    harness.fixture.componentInstance.fromUnitId.set('unit-gram');
    harness.fixture.componentInstance.toUnitId.set('unit-kilogram');
    await convert(harness, (req) => req.flush(conversionPayload()));

    const card = text(harness.fixture);
    expect(card).toContain('Same-dimension factor');
    expect(card).toContain('× (1 gram-per-base ÷ 1000 kilogram-per-base)');
    // The symbols are not the whole message: the sentence beneath is what a screen reader can rely on.
    expect(card).toContain("Multiplied by the ratio of the two units' own base factors.");
    expect(card).toContain('2 decimal places');
    expect(card).toContain('Half to even');
  });

  it('shows the exact fraction beside a rounded amount', async () => {
    const harness = await createFixture();
    harness.fixture.componentInstance.quantityValue.set(1000);
    harness.fixture.componentInstance.fromUnitId.set('unit-gram');
    harness.fixture.componentInstance.toUnitId.set('unit-kilogram');
    await convert(harness, (req) => req.flush(conversionPayload()));

    expect(text(harness.fixture)).toContain('exact 1/4');
  });

  it('cites the density source when one was used', async () => {
    const harness = await createFixture();
    harness.fixture.componentInstance.quantityValue.set(2);
    harness.fixture.componentInstance.fromUnitId.set('unit-cup');
    harness.fixture.componentInstance.toUnitId.set('unit-gram');
    await convert(harness, (req) =>
      req.flush(
        conversionPayload({
          method: 'IngredientDensity',
          source: 'USDA FoodData Central, entry 169761',
          formula: '× density 120 gram per 1 cup',
        }),
      ),
    );

    const card = text(harness.fixture);
    expect(card).toContain('Ingredient density');
    expect(card).toContain('USDA FoodData Central, entry 169761');
    expect(card).toContain('Bridged between weight and volume');
  });

  // Said in words rather than left as a missing button: a control that is simply absent reads as an
  // oversight, and this names what would bring it back.
  it('says why a converted amount cannot be saved back yet', async () => {
    const harness = await createFixture();
    harness.fixture.componentInstance.quantityValue.set(1000);
    harness.fixture.componentInstance.fromUnitId.set('unit-gram');
    harness.fixture.componentInstance.toUnitId.set('unit-kilogram');
    await convert(harness, (req) => req.flush(conversionPayload()));

    expect(text(harness.fixture)).toContain('needs ingredient saving');
    expect(harness.fixture.nativeElement.querySelector('.apply')).toBeNull();
  });

  it('shows no density source for a same-dimension conversion', async () => {
    const harness = await createFixture();
    harness.fixture.componentInstance.quantityValue.set(1000);
    harness.fixture.componentInstance.fromUnitId.set('unit-gram');
    harness.fixture.componentInstance.toUnitId.set('unit-kilogram');
    await convert(harness, (req) => req.flush(conversionPayload()));

    expect(text(harness.fixture)).not.toContain('Density source');
  });

  // ---- Unsupported conversions ----

  const refusals: readonly { readonly name: string; readonly message: string }[] = [
    { name: 'incompatible units', message: 'These units cannot be converted between each other.' },
    {
      name: 'a missing density',
      message: 'This conversion needs an ingredient-specific density reference, which is not yet available.',
    },
    { name: 'a temperature unit', message: 'Temperature units use the temperature calculation instead.' },
    { name: 'a unit that is gone', message: 'That unit is not available.' },
  ];

  for (const refusal of refusals) {
    it(`shows ${refusal.name} as a refusal and never as a result`, async () => {
      const harness = await createFixture();
      harness.fixture.componentInstance.quantityValue.set(180);
      harness.fixture.componentInstance.fromUnitId.set('unit-celsius');
      harness.fixture.componentInstance.toUnitId.set('unit-gram');

      await convert(harness, (req) =>
        req.flush(
          { code: 'recipes.unit-conversion.invalid_request', errors: { toUnitId: [refusal.message] } },
          { status: 400, statusText: 'Bad Request' },
        ),
      );

      expect(text(harness.fixture)).toContain(refusal.message);
      expect(harness.fixture.nativeElement.querySelector('.result-card')).toBeNull();
    });
  }

  // The rule the RESTRICTION states outright. A converted amount left beside a refusal about different units
  // reads as a qualified success.
  it('clears a previous result when a later conversion is refused', async () => {
    const harness = await createFixture();
    harness.fixture.componentInstance.quantityValue.set(1000);
    harness.fixture.componentInstance.fromUnitId.set('unit-gram');
    harness.fixture.componentInstance.toUnitId.set('unit-kilogram');
    await convert(harness, (req) => req.flush(conversionPayload()));
    expect(harness.fixture.nativeElement.querySelector('.result-card')).not.toBeNull();

    harness.fixture.componentInstance.fromUnitId.set('unit-cup');
    await convert(harness, (req) =>
      req.flush(
        {
          code: 'recipes.unit-conversion.invalid_request',
          errors: { toUnitId: ['These units cannot be converted between each other.'] },
        },
        { status: 400, statusText: 'Bad Request' },
      ),
    );

    expect(harness.fixture.nativeElement.querySelector('.result-card')).toBeNull();
    expect(text(harness.fixture)).toContain('cannot be converted between each other');
  });

  it('clears a result when the recipe moves past the version it was computed against', async () => {
    const harness = await createFixture();
    harness.fixture.componentInstance.quantityValue.set(1000);
    harness.fixture.componentInstance.fromUnitId.set('unit-gram');
    harness.fixture.componentInstance.toUnitId.set('unit-kilogram');
    await convert(harness, (req) => req.flush(conversionPayload()));

    harness.fixture.componentRef.setInput('sourceVersionNumber', 4);
    harness.fixture.detectChanges();

    expect(harness.fixture.nativeElement.querySelector('.result-card')).toBeNull();
  });

  // ---- Local refusals ----

  it('refuses an empty amount without calling the server', async () => {
    const { fixture, http } = await createFixture();
    fixture.componentInstance.fromUnitId.set('unit-gram');
    fixture.componentInstance.toUnitId.set('unit-kilogram');

    await fixture.componentInstance.convert();
    fixture.detectChanges();

    http.expectNone(CONVERT_URL);
    expect(text(fixture)).toContain('Enter an amount.');
  });

  it('refuses an unchosen unit without calling the server', async () => {
    const { fixture, http } = await createFixture();
    fixture.componentInstance.quantityValue.set(1000);

    await fixture.componentInstance.convert();
    fixture.detectChanges();

    http.expectNone(CONVERT_URL);
    expect(text(fixture)).toContain('Choose both units.');
  });

  it('refuses to convert a recipe with no saved version, and says what to do', async () => {
    await TestBed.configureTestingModule({
      imports: [RecipeUnitConversionComponent],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();

    const http = TestBed.inject(HttpTestingController);
    const runtimeConfig = TestBed.inject(RuntimeConfigService);
    const loading = runtimeConfig.load();
    http.expectOne('/runtime-config.json').flush({ gatewayUrl: 'https://gateway.example' });
    await loading;

    const fixture = TestBed.createComponent(RecipeUnitConversionComponent);
    fixture.componentRef.setInput('workspaceSlug', 'cozy-fall');
    fixture.componentRef.setInput('recipeId', RECIPE_ID);
    fixture.componentRef.setInput('sourceVersionNumber', null);
    fixture.detectChanges();
    http.expectOne((request) => request.url === UNITS_URL).flush({ items: CATALOGUE, nextCursor: null });
    await fixture.whenStable();
    fixture.detectChanges();

    expect(text(fixture)).toContain('no saved version yet');
    const submit: HTMLButtonElement = fixture.nativeElement.querySelector('button[type="submit"]');
    expect(submit.disabled).toBeTrue();
  });

  it('explains a version that no longer exists', async () => {
    const harness = await createFixture();
    harness.fixture.componentInstance.quantityValue.set(1);
    harness.fixture.componentInstance.fromUnitId.set('unit-gram');
    harness.fixture.componentInstance.toUnitId.set('unit-kilogram');
    await convert(harness, (req) =>
      req.flush({ code: 'recipes.version.not_found' }, { status: 404, statusText: 'Not Found' }),
    );

    expect(text(harness.fixture)).toContain('That version no longer exists');
  });

  it('offers a retry when the conversion could not be reached', async () => {
    const harness = await createFixture();
    harness.fixture.componentInstance.quantityValue.set(1);
    harness.fixture.componentInstance.fromUnitId.set('unit-gram');
    harness.fixture.componentInstance.toUnitId.set('unit-kilogram');
    await convert(harness, (req) => req.flush({}, { status: 500, statusText: 'Server Error' }));

    expect(text(harness.fixture)).toContain('try again');
  });

  // ---- Accessibility ----

  it('announces a landed result politely, without moving focus', async () => {
    const harness = await createFixture();
    harness.fixture.componentInstance.quantityValue.set(1000);
    harness.fixture.componentInstance.fromUnitId.set('unit-gram');
    harness.fixture.componentInstance.toUnitId.set('unit-kilogram');
    await convert(harness, (req) => req.flush(conversionPayload()));

    // The announcement is a persistent region whose text changes, not the card itself: a live region
    // inserted already holding its content is not reliably announced.
    const region: HTMLElement = harness.fixture.nativeElement.querySelector('[role="status"][aria-live="polite"]');
    expect(region.getAttribute('aria-live')).toBe('polite');
    expect(region.textContent?.trim().length).toBeGreaterThan(0);
    expect(harness.fixture.nativeElement.querySelector('.result-card').getAttribute('role')).toBeNull();
  });

  it('presents the working as term-and-value pairs', async () => {
    const harness = await createFixture();
    harness.fixture.componentInstance.quantityValue.set(1000);
    harness.fixture.componentInstance.fromUnitId.set('unit-gram');
    harness.fixture.componentInstance.toUnitId.set('unit-kilogram');
    await convert(harness, (req) => req.flush(conversionPayload()));

    const terms: HTMLElement[] = Array.from(harness.fixture.nativeElement.querySelectorAll('.result-detail dt'));
    expect(terms.map((term) => term.textContent?.trim())).toEqual([
      'Method',
      'Formula',
      'Precision',
      'Rounding',
    ]);
  });

  // `disabled` plus a `title` announces nothing useful; the reason has to be in the option's own text.
  it('carries each unresolved reason in the option text itself', async () => {
    const { fixture } = await createFixture();
    const texts = optionTexts(fixture, fixture.componentInstance.prefillFieldId);

    expect(texts.some((option) => option.includes('no unit recorded'))).toBeTrue();
    expect(texts.some((option) => option.includes('no amount recorded'))).toBeTrue();
  });

  it('marks a refused field invalid', async () => {
    const harness = await createFixture();
    harness.fixture.componentInstance.quantityValue.set(1);
    harness.fixture.componentInstance.fromUnitId.set('unit-gram');
    harness.fixture.componentInstance.toUnitId.set('unit-celsius');
    await convert(harness, (req) =>
      req.flush(
        {
          code: 'recipes.unit-conversion.invalid_request',
          errors: { toUnitId: ['Temperature units use the temperature calculation instead.'] },
        },
        { status: 400, statusText: 'Bad Request' },
      ),
    );

    const select: HTMLSelectElement = harness.fixture.nativeElement.querySelector(
      `#${harness.fixture.componentInstance.toFieldId}`,
    );
    expect(select.getAttribute('aria-invalid')).toBe('true');
  });
});
