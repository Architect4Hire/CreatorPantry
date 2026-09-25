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

async function createFixture(
  options: { catalogueFails?: boolean; sourceVersionNumber?: number | null } = {},
): Promise<Harness> {
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

async function render(
  harness: Harness,
  respond: (request: ReturnType<HttpTestingController['expectOne']>) => void,
): Promise<void> {
  const call = harness.fixture.componentInstance.render();
  respond(harness.http.expectOne(DISPLAY_URL));
  await call;
  harness.fixture.detectChanges();
}

function text(fixture: ComponentFixture<RecipeDisplayNormalizationComponent>): string {
  return (fixture.nativeElement as HTMLElement).textContent ?? '';
}

function canonicalPairs(fixture: ComponentFixture<RecipeDisplayNormalizationComponent>): string[] {
  return Array.from(fixture.nativeElement.querySelectorAll('.canonical-pair dt, .canonical-pair dd')).map(
    (node) => ((node as HTMLElement).textContent ?? '').replace(/\s+/g, ' ').trim(),
  );
}

describe('RecipeDisplayNormalizationComponent', () => {
  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('says up front that this changes nothing', async () => {
    const { fixture } = await createFixture();

    expect(text(fixture)).toContain('Presentation only');
    expect(text(fixture)).toContain("The recipe's own quantity stays exactly as it is");
  });

  // ---- Canonical versus display ----

  it('shows the canonical value and the rendered text side by side, each labelled', async () => {
    const harness = await createFixture();
    harness.fixture.componentInstance.value.set(1.5);
    harness.fixture.componentInstance.unitId.set('unit-cup');
    await render(harness, (req) => req.flush(payload()));

    expect(canonicalPairs(harness.fixture)).toEqual([
      'Canonical value',
      '1.5 cup unchanged',
      'How it reads',
      '1½ cups',
    ]);
  });

  it('shows a range canonically as both bounds', async () => {
    const harness = await createFixture();
    harness.fixture.componentInstance.value.set(1);
    harness.fixture.componentInstance.upperValue.set(1.5);
    harness.fixture.componentInstance.unitId.set('unit-cup');
    await render(harness, (req) =>
      req.flush(payload({ text: '1–1½ cups', presentation: 'Whole', upperPresentation: 'Fraction' })),
    );

    expect(canonicalPairs(harness.fixture)).toContain('1 to 1.5 cup unchanged');
    expect(canonicalPairs(harness.fixture)).toContain('1–1½ cups');
  });

  // Worded as a permanent rule, not a missing feature: ING-006 keeps the canonical quantity unchanged, so
  // there is nothing an apply could ever write here.
  it('says there is nothing to apply, as a rule rather than as a gap', async () => {
    const harness = await createFixture();
    harness.fixture.componentInstance.value.set(1.5);
    harness.fixture.componentInstance.unitId.set('unit-cup');
    await render(harness, (req) => req.flush(payload()));

    expect(text(harness.fixture)).toContain('There is nothing to apply');
    expect(text(harness.fixture)).not.toContain("isn't built yet");
    expect(harness.fixture.nativeElement.querySelector('.apply')).toBeNull();
  });

  it('repeats that the canonical quantity is unchanged on the card itself', async () => {
    const harness = await createFixture();
    harness.fixture.componentInstance.value.set(1.5);
    harness.fixture.componentInstance.unitId.set('unit-cup');
    await render(harness, (req) => req.flush(payload()));

    expect(text(harness.fixture)).toContain('nothing here is saved or fed back into the recipe');
  });

  // The canonical half must come from what was sent, not be rebuilt from the rendering.
  it('keeps the canonical value as sent even after the form is edited', async () => {
    const harness = await createFixture();
    harness.fixture.componentInstance.value.set(1.5);
    harness.fixture.componentInstance.unitId.set('unit-cup');
    await render(harness, (req) => req.flush(payload()));

    harness.fixture.componentInstance.value.set(99);
    harness.fixture.detectChanges();

    expect(canonicalPairs(harness.fixture)).toContain('1.5 cup unchanged');
  });

  // ---- How it was rendered ----

  const presentations: readonly { readonly kind: string; readonly label: string; readonly explanation: string }[] = [
    { kind: 'Whole', label: 'Whole number', explanation: 'no fractional remainder' },
    { kind: 'Fraction', label: 'Kitchen fraction', explanation: 'matched a common kitchen fraction' },
    { kind: 'Decimal', label: 'Rounded decimal', explanation: 'No common kitchen fraction matched' },
  ];

  for (const each of presentations) {
    it(`explains the ${each.kind} presentation in words`, async () => {
      const harness = await createFixture();
      harness.fixture.componentInstance.value.set(1.5);
      harness.fixture.componentInstance.unitId.set('unit-cup');
      await render(harness, (req) => req.flush(payload({ presentation: each.kind })));

      expect(text(harness.fixture)).toContain(each.label);
      expect(text(harness.fixture)).toContain(each.explanation);
    });
  }

  it('names the upper bound presentation for a range', async () => {
    const harness = await createFixture();
    harness.fixture.componentInstance.value.set(1);
    harness.fixture.componentInstance.upperValue.set(1.5);
    harness.fixture.componentInstance.unitId.set('unit-cup');
    await render(harness, (req) =>
      req.flush(payload({ text: '1–1½ cups', presentation: 'Whole', upperPresentation: 'Fraction' })),
    );

    expect(text(harness.fixture)).toContain('Upper bound');
  });

  it('shows no upper bound row for a single value', async () => {
    const harness = await createFixture();
    harness.fixture.componentInstance.value.set(1.5);
    harness.fixture.componentInstance.unitId.set('unit-cup');
    await render(harness, (req) => req.flush(payload()));

    expect(text(harness.fixture)).not.toContain('Upper bound');
  });

  it('shows the precision and the rounding mode in words', async () => {
    const harness = await createFixture();
    harness.fixture.componentInstance.value.set(1.025);
    harness.fixture.componentInstance.unitId.set('unit-cup');
    await render(harness, (req) => req.flush(payload({ text: '1.02 cups', presentation: 'Decimal' })));

    expect(text(harness.fixture)).toContain('2 decimal places');
    expect(text(harness.fixture)).toContain('Half to even');
  });

  // ---- Inputs ----

  it('sends the value, unit and presentation choices', async () => {
    const harness = await createFixture();
    harness.fixture.componentInstance.value.set(1.5);
    harness.fixture.componentInstance.unitId.set('unit-cup');

    await render(harness, (req) => {
      expect(req.request.body).toEqual({
        sourceVersionNumber: 3,
        value: 1.5,
        unitId: 'unit-cup',
        precision: 2,
        useAbbreviation: false,
        rounding: 'ToEven',
      });
      req.flush(payload());
    });
  });

  it('sends the abbreviation choice and the chosen rounding mode', async () => {
    const harness = await createFixture();
    harness.fixture.componentInstance.value.set(1.025);
    harness.fixture.componentInstance.unitId.set('unit-cup');
    harness.fixture.componentInstance.useAbbreviation.set(true);
    harness.fixture.componentInstance.setRounding('AwayFromZero');

    await render(harness, (req) => {
      expect(req.request.body).toEqual(
        jasmine.objectContaining({ useAbbreviation: true, rounding: 'AwayFromZero' }),
      );
      req.flush(payload({ text: '1.03 c', presentation: 'Decimal', rounding: 'AwayFromZero' }));
    });

    expect(text(harness.fixture)).toContain('Half away from zero');
  });

  // Two, not the five MidpointRounding defines: the other three are meaningless for an exact rational and
  // the server refuses them, so offering them meant three of the choices failed — intermittently, because a
  // value matching a kitchen fraction never reaches the rounding step at all.
  it('offers only the rounding modes the calculation supports', async () => {
    const { fixture } = await createFixture();
    const select: HTMLSelectElement = fixture.nativeElement.querySelector(
      `#${fixture.componentInstance.roundingFieldId}`,
    );

    expect(Array.from(select.options).map((option) => option.value)).toEqual(['ToEven', 'AwayFromZero']);
  });

  // The select offers full sentences, because "ToEven" documents nothing (CALC-002).
  it('names each rounding mode in words rather than by enum name', async () => {
    const { fixture } = await createFixture();
    const select: HTMLSelectElement = fixture.nativeElement.querySelector(
      `#${fixture.componentInstance.roundingFieldId}`,
    );

    expect(select.options[0].textContent).toContain('Half to even');
    expect(select.options[0].textContent).not.toBe('ToEven');
  });

  it('ignores a rounding value the route does not accept', async () => {
    const { fixture } = await createFixture();

    fixture.componentInstance.setRounding('Stochastic');

    expect(fixture.componentInstance.rounding()).toBe('ToEven');
  });

  // ---- Prefill ----

  it('lists every saved line, disabling the ones that cannot fill the form and saying why', async () => {
    const { fixture } = await createFixture();
    const select: HTMLSelectElement = fixture.nativeElement.querySelector(
      `#${fixture.componentInstance.prefillFieldId}`,
    );
    const options = Array.from(select.options);

    expect(options.map((option) => option.textContent?.trim())).toEqual([
      'Choose a line…',
      '1½ cups all-purpose flour',
      'salt, to taste — no amount recorded',
    ]);
    expect(options.map((option) => option.disabled)).toEqual([false, false, true]);
  });

  it('fills the value and unit from a resolved line', async () => {
    const { fixture } = await createFixture();

    fixture.componentInstance.prefillFrom('line-1');

    expect(fixture.componentInstance.value()).toBe(1.5);
    expect(fixture.componentInstance.unitId()).toBe('unit-cup');
  });

  it('fills nothing from an unresolved line', async () => {
    const { fixture } = await createFixture();

    fixture.componentInstance.prefillFrom('line-2');

    expect(fixture.componentInstance.value()).toBeNull();
  });

  // ---- Refusals ----

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

  it('refuses an empty value without calling the server', async () => {
    const { fixture, http } = await createFixture();
    fixture.componentInstance.unitId.set('unit-cup');

    await fixture.componentInstance.render();
    fixture.detectChanges();

    http.expectNone(DISPLAY_URL);
    expect(text(fixture)).toContain('Enter a value.');
  });

  it('refuses an unchosen unit without calling the server', async () => {
    const { fixture, http } = await createFixture();
    fixture.componentInstance.value.set(1.5);

    await fixture.componentInstance.render();
    fixture.detectChanges();

    http.expectNone(DISPLAY_URL);
    expect(text(fixture)).toContain('Choose a unit.');
  });

  it('shows an upper value that is not above the value against its field', async () => {
    const harness = await createFixture();
    harness.fixture.componentInstance.value.set(2);
    harness.fixture.componentInstance.upperValue.set(2);
    harness.fixture.componentInstance.unitId.set('unit-cup');

    await render(harness, (req) =>
      req.flush(
        {
          code: 'recipes.display-normalization.invalid_request',
          errors: { upperValue: ['The upper value must be greater than the value.'] },
        },
        { status: 400, statusText: 'Bad Request' },
      ),
    );

    expect(text(harness.fixture)).toContain('The upper value must be greater than the value.');
    const input: HTMLInputElement = harness.fixture.nativeElement.querySelector(
      `#${harness.fixture.componentInstance.upperFieldId}`,
    );
    expect(input.getAttribute('aria-invalid')).toBe('true');
  });

  it('clears a previous card when a later request is refused', async () => {
    const harness = await createFixture();
    harness.fixture.componentInstance.value.set(1.5);
    harness.fixture.componentInstance.unitId.set('unit-cup');
    await render(harness, (req) => req.flush(payload()));
    expect(harness.fixture.nativeElement.querySelector('.result-card')).not.toBeNull();

    harness.fixture.componentInstance.upperValue.set(1);
    await render(harness, (req) =>
      req.flush(
        {
          code: 'recipes.display-normalization.invalid_request',
          errors: { upperValue: ['The upper value must be greater than the value.'] },
        },
        { status: 400, statusText: 'Bad Request' },
      ),
    );

    expect(harness.fixture.nativeElement.querySelector('.result-card')).toBeNull();
  });

  it('clears a card when the recipe moves past the version it was computed against', async () => {
    const harness = await createFixture();
    harness.fixture.componentInstance.value.set(1.5);
    harness.fixture.componentInstance.unitId.set('unit-cup');
    await render(harness, (req) => req.flush(payload()));

    harness.fixture.componentRef.setInput('sourceVersionNumber', 4);
    harness.fixture.detectChanges();

    expect(harness.fixture.nativeElement.querySelector('.result-card')).toBeNull();
  });

  it('refuses to render for a recipe with no saved version, and says what to do', async () => {
    const { fixture, http } = await createFixture({ sourceVersionNumber: null });

    expect(text(fixture)).toContain('no saved version yet');
    const submit: HTMLButtonElement = fixture.nativeElement.querySelector('button[type="submit"]');
    expect(submit.disabled).toBeTrue();

    fixture.componentInstance.value.set(1.5);
    fixture.componentInstance.unitId.set('unit-cup');
    await fixture.componentInstance.render();
    http.expectNone(DISPLAY_URL);
  });

  it('offers a retry when the rendering could not be reached', async () => {
    const harness = await createFixture();
    harness.fixture.componentInstance.value.set(1.5);
    harness.fixture.componentInstance.unitId.set('unit-cup');
    await render(harness, (req) => req.flush({}, { status: 500, statusText: 'Server Error' }));

    expect(text(harness.fixture)).toContain('try again');
  });

  // ---- Accessibility ----

  it('announces a landed card politely, without moving focus', async () => {
    const harness = await createFixture();
    harness.fixture.componentInstance.value.set(1.5);
    harness.fixture.componentInstance.unitId.set('unit-cup');
    await render(harness, (req) => req.flush(payload()));

    // The announcement is a persistent region whose text changes, not the card itself: a live region
    // inserted already holding its content is not reliably announced.
    const region: HTMLElement = harness.fixture.nativeElement.querySelector('[role="status"][aria-live="polite"]');
    expect(region.getAttribute('aria-live')).toBe('polite');
    expect(region.textContent?.trim().length).toBeGreaterThan(0);
    expect(harness.fixture.nativeElement.querySelector('.result-card').getAttribute('role')).toBeNull();
  });

  it('pairs the canonical and rendered values as terms and values', async () => {
    const harness = await createFixture();
    harness.fixture.componentInstance.value.set(1.5);
    harness.fixture.componentInstance.unitId.set('unit-cup');
    await render(harness, (req) => req.flush(payload()));

    const terms: HTMLElement[] = Array.from(
      harness.fixture.nativeElement.querySelectorAll('.canonical-pair dt'),
    );
    expect(terms.map((term) => term.textContent?.trim())).toEqual(['Canonical value', 'How it reads']);
  });

  it('pairs the caution glyph with its own words', async () => {
    const harness = await createFixture();
    harness.fixture.componentInstance.value.set(1.5);
    harness.fixture.componentInstance.unitId.set('unit-cup');
    await render(harness, (req) => req.flush(payload()));

    const glyph: HTMLElement = harness.fixture.nativeElement.querySelector('.caution-glyph');
    expect(glyph.getAttribute('aria-hidden')).toBe('true');
    expect((glyph.parentElement?.textContent ?? '').trim().length).toBeGreaterThan(20);
  });
});
