import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { RuntimeConfigService } from '../../core/runtime-config.service';
import { RecipeInstructionGroup } from '../../models/recipe.models';
import { RecipeTemperatureConversionComponent } from './recipe-temperature-conversion.component';

const RECIPE_ID = '2a1b7c6d-0000-4000-8000-000000000001';
const CONVERT_URL = `https://gateway.example/api/v1/workspaces/cozy-fall/recipes/${RECIPE_ID}/calculations/convert-temperature`;
const UNITS_URL = 'https://gateway.example/api/v1/reference/units';

function unit(id: string, code: string): Record<string, unknown> {
  return {
    id,
    code,
    displayName: code,
    pluralName: `${code}s`,
    abbreviation: code === 'celsius' ? '°C' : '°F',
    dimension: 'Temperature',
    system: 'Metric',
    baseUnitFactor: null,
    displayPrecision: 0,
  };
}

const CATALOGUE = [unit('unit-celsius', 'celsius'), unit('unit-fahrenheit', 'fahrenheit'), unit('unit-kelvin', 'kelvin')];

/** Steps covering every case: a recorded temperature, one with no scale, prose heat, and an unknown scale. */
const SAVED_STEPS: readonly RecipeInstructionGroup[] = [
  {
    id: 'group-1',
    title: null,
    sortOrder: 0,
    steps: [
      {
        id: 'step-1',
        sortOrder: 0,
        text: 'Heat the oil over medium-high',
        techniqueId: null,
        durationMinutes: null,
        temperatureValue: null,
        temperatureUnitId: null,
        note: null,
      },
      {
        id: 'step-2',
        sortOrder: 1,
        text: 'Bake until golden',
        techniqueId: null,
        durationMinutes: 40,
        temperatureValue: 180,
        temperatureUnitId: 'unit-celsius',
        note: null,
      },
      {
        id: 'step-3',
        sortOrder: 2,
        text: 'Hold the oven at 350',
        techniqueId: null,
        durationMinutes: null,
        temperatureValue: 350,
        temperatureUnitId: null,
        note: null,
      },
      {
        id: 'step-4',
        sortOrder: 3,
        text: 'Chill the dough',
        techniqueId: null,
        durationMinutes: null,
        temperatureValue: 277,
        temperatureUnitId: 'unit-kelvin',
        note: null,
      },
    ],
  },
];

function conversionPayload(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    sourceVersionNumber: 3,
    result: {
      sourceValue: { numerator: '180', denominator: '1' },
      sourceScale: 'Celsius',
      convertedValue: { numerator: '356', denominator: '1' },
      convertedDisplayValue: 356,
      convertedScale: 'Fahrenheit',
      precision: 0,
      rounding: 'ToEven',
      formula: '°F = °C × 9/5 + 32',
      ovenModeContext: null,
      safetyNote: null,
      ...overrides,
    },
  };
}

interface Harness {
  readonly fixture: ComponentFixture<RecipeTemperatureConversionComponent>;
  readonly http: HttpTestingController;
}

async function createFixture(options: { sourceVersionNumber?: number | null } = {}): Promise<Harness> {
  await TestBed.configureTestingModule({
    imports: [RecipeTemperatureConversionComponent],
    providers: [provideHttpClient(), provideHttpClientTesting()],
  }).compileComponents();

  const http = TestBed.inject(HttpTestingController);
  const runtimeConfig = TestBed.inject(RuntimeConfigService);
  const loading = runtimeConfig.load();
  http.expectOne('/runtime-config.json').flush({ gatewayUrl: 'https://gateway.example' });
  await loading;

  const fixture = TestBed.createComponent(RecipeTemperatureConversionComponent);
  fixture.componentRef.setInput('workspaceSlug', 'cozy-fall');
  fixture.componentRef.setInput('recipeId', RECIPE_ID);
  fixture.componentRef.setInput(
    'sourceVersionNumber',
    options.sourceVersionNumber === undefined ? 3 : options.sourceVersionNumber,
  );
  fixture.componentRef.setInput('savedInstructionGroups', SAVED_STEPS);
  fixture.detectChanges();

  http.expectOne((request) => request.url === UNITS_URL).flush({ items: CATALOGUE, nextCursor: null });
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

function text(fixture: ComponentFixture<RecipeTemperatureConversionComponent>): string {
  return (fixture.nativeElement as HTMLElement).textContent ?? '';
}

describe('RecipeTemperatureConversionComponent', () => {
  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('says that nothing is saved before any conversion is made', async () => {
    const { fixture } = await createFixture();

    expect(text(fixture)).toContain('nothing here is saved');
    expect(text(fixture)).toContain('Enter a temperature and choose two scales');
  });

  // ---- Unresolved values ----

  it('lists every step, disabling the one whose heat is only described in words', async () => {
    const { fixture } = await createFixture();
    const select: HTMLSelectElement = fixture.nativeElement.querySelector(
      `#${fixture.componentInstance.prefillFieldId}`,
    );
    const options = Array.from(select.options);

    expect(options[1].textContent?.trim()).toBe('Step 1 — Heat the oil over medium-high — no temperature recorded');
    expect(options[1].disabled).toBeTrue();
    expect(options[2].textContent?.trim()).toBe('Step 2 — Bake until golden');
    expect(options[2].disabled).toBeFalse();
  });

  it('fills the value and the scale from a step that records both', async () => {
    const { fixture } = await createFixture();

    fixture.componentInstance.prefillFrom('step-2');
    fixture.detectChanges();

    expect(fixture.componentInstance.temperatureValue()).toBe(180);
    expect(fixture.componentInstance.fromScale()).toBe('Celsius');
    expect(fixture.componentInstance.scaleNote()).toBeNull();
  });

  // Reading 350 as Celsius when the recipe meant Fahrenheit is the error this avoids — so the scale is left
  // unchosen and said out loud rather than defaulted.
  it('fills only the value from a step with no recorded scale, and says the scale is missing', async () => {
    const { fixture } = await createFixture();

    fixture.componentInstance.prefillFrom('step-3');
    fixture.detectChanges();

    expect(fixture.componentInstance.temperatureValue()).toBe(350);
    expect(fixture.componentInstance.fromScale()).toBeNull();
    expect(text(fixture)).toContain('Scale not recorded');
    expect(text(fixture)).toContain('not which scale it is in');
  });

  it('leaves the scale unresolved for a temperature unit this calculation does not know', async () => {
    const { fixture } = await createFixture();

    fixture.componentInstance.prefillFrom('step-4');
    fixture.detectChanges();

    expect(fixture.componentInstance.temperatureValue()).toBe(277);
    expect(fixture.componentInstance.fromScale()).toBeNull();
    expect(text(fixture)).toContain('Scale not recorded');
  });

  it('fills nothing from a step with no recorded temperature', async () => {
    const { fixture } = await createFixture();

    fixture.componentInstance.prefillFrom('step-1');

    expect(fixture.componentInstance.temperatureValue()).toBeNull();
  });

  // ---- Converting ----

  it('sends the value, both scales and the precision', async () => {
    const harness = await createFixture();
    harness.fixture.componentInstance.temperatureValue.set(180);
    harness.fixture.componentInstance.setFromScale('Celsius');

    await convert(harness, (req) => {
      expect(req.request.body).toEqual({
        sourceVersionNumber: 3,
        value: 180,
        fromScale: 'Celsius',
        toScale: 'Fahrenheit',
        precision: 0,
        ovenModeContext: null,
        safetyNote: null,
      });
      req.flush(conversionPayload());
    });

    expect(text(harness.fixture)).toContain('356 °F');
  });

  it('shows the formula in symbols and in words, with precision and rounding', async () => {
    const harness = await createFixture();
    harness.fixture.componentInstance.temperatureValue.set(180);
    harness.fixture.componentInstance.setFromScale('Celsius');
    await convert(harness, (req) => req.flush(conversionPayload()));

    const card = text(harness.fixture);
    expect(card).toContain('°F = °C × 9/5 + 32');
    expect(card).toContain('Multiplied by nine fifths, then 32 added.');
    expect(card).toContain('0 decimal places');
    expect(card).toContain('Half to even');
  });

  it('renders a same-scale conversion as the identity it is', async () => {
    const harness = await createFixture();
    harness.fixture.componentInstance.temperatureValue.set(74);
    harness.fixture.componentInstance.setFromScale('Celsius');
    harness.fixture.componentInstance.setToScale('Celsius');
    await convert(harness, (req) =>
      req.flush(
        conversionPayload({
          convertedValue: { numerator: '74', denominator: '1' },
          convertedDisplayValue: 74,
          convertedScale: 'Celsius',
          formula: 'identity — source and target scale are the same',
        }),
      ),
    );

    expect(text(harness.fixture)).toContain('74 °C');
    expect(text(harness.fixture)).toContain('Unchanged: the two scales are the same.');
  });

  it('sends a negative temperature without refusing it', async () => {
    const harness = await createFixture();
    harness.fixture.componentInstance.temperatureValue.set(-18);
    harness.fixture.componentInstance.setFromScale('Celsius');

    await convert(harness, (req) => {
      expect(req.request.body).toEqual(jasmine.objectContaining({ value: -18 }));
      req.flush(conversionPayload({ convertedDisplayValue: 0 }));
    });

    expect(text(harness.fixture)).not.toContain('Enter a temperature.');
  });

  // ---- Pass-through ----

  it('sends the oven mode and safety note, and shows them back under a caution', async () => {
    const harness = await createFixture();
    harness.fixture.componentInstance.temperatureValue.set(180);
    harness.fixture.componentInstance.setFromScale('Celsius');
    harness.fixture.componentInstance.ovenModeContext.set('convection');
    harness.fixture.componentInstance.safetyNote.set('until the centre reads 74 °C');

    await convert(harness, (req) => {
      expect(req.request.body).toEqual(
        jasmine.objectContaining({ ovenModeContext: 'convection', safetyNote: 'until the centre reads 74 °C' }),
      );
      req.flush(
        conversionPayload({ ovenModeContext: 'convection', safetyNote: 'until the centre reads 74 °C' }),
      );
    });

    const card = text(harness.fixture);
    expect(card).toContain('Carried through unchanged');
    expect(card).toContain('convection');
    expect(card).toContain('until the centre reads 74 °C');
    // recipes.md: an echoed note is not a checked one, and this is exactly where that would be misread.
    expect(card).toContain('does not confirm that any temperature is safe');
  });

  it('sends blank context as null rather than as an empty string', async () => {
    const harness = await createFixture();
    harness.fixture.componentInstance.temperatureValue.set(180);
    harness.fixture.componentInstance.setFromScale('Celsius');
    harness.fixture.componentInstance.ovenModeContext.set('   ');

    await convert(harness, (req) => {
      expect(req.request.body).toEqual(jasmine.objectContaining({ ovenModeContext: null }));
      req.flush(conversionPayload());
    });
  });

  it('shows no carried-through block when nothing was carried through', async () => {
    const harness = await createFixture();
    harness.fixture.componentInstance.temperatureValue.set(180);
    harness.fixture.componentInstance.setFromScale('Celsius');
    await convert(harness, (req) => req.flush(conversionPayload()));

    expect(text(harness.fixture)).not.toContain('Carried through unchanged');
    expect(text(harness.fixture)).not.toContain('does not confirm that any temperature is safe');
  });

  // ---- Refusals ----

  it('refuses an empty temperature without calling the server', async () => {
    const { fixture, http } = await createFixture();
    fixture.componentInstance.setFromScale('Celsius');

    await fixture.componentInstance.convert();
    fixture.detectChanges();

    http.expectNone(CONVERT_URL);
    expect(text(fixture)).toContain('Enter a temperature.');
  });

  it('refuses to convert before a scale is chosen', async () => {
    const { fixture, http } = await createFixture();
    fixture.componentInstance.temperatureValue.set(180);

    await fixture.componentInstance.convert();
    fixture.detectChanges();

    http.expectNone(CONVERT_URL);
    expect(text(fixture)).toContain('Choose which scale this temperature is in.');
  });

  it('shows the refusal for a negative precision against its field', async () => {
    const harness = await createFixture();
    harness.fixture.componentInstance.temperatureValue.set(180);
    harness.fixture.componentInstance.setFromScale('Celsius');
    harness.fixture.componentInstance.precisionValue.set(-1);

    await convert(harness, (req) =>
      req.flush(
        {
          code: 'recipes.temperature-conversion.invalid_request',
          errors: { precision: ['Precision cannot be negative.'] },
        },
        { status: 400, statusText: 'Bad Request' },
      ),
    );

    expect(text(harness.fixture)).toContain('Precision cannot be negative.');
    expect(harness.fixture.nativeElement.querySelector('.result-card')).toBeNull();
  });

  it('clears a previous result when a later conversion is refused', async () => {
    const harness = await createFixture();
    harness.fixture.componentInstance.temperatureValue.set(180);
    harness.fixture.componentInstance.setFromScale('Celsius');
    await convert(harness, (req) => req.flush(conversionPayload()));
    expect(harness.fixture.nativeElement.querySelector('.result-card')).not.toBeNull();

    harness.fixture.componentInstance.precisionValue.set(-1);
    await convert(harness, (req) =>
      req.flush(
        {
          code: 'recipes.temperature-conversion.invalid_request',
          errors: { precision: ['Precision cannot be negative.'] },
        },
        { status: 400, statusText: 'Bad Request' },
      ),
    );

    expect(harness.fixture.nativeElement.querySelector('.result-card')).toBeNull();
  });

  it('refuses to convert a recipe with no saved version, and says what to do', async () => {
    const { fixture, http } = await createFixture({ sourceVersionNumber: null });

    expect(text(fixture)).toContain('no saved version yet');
    const submit: HTMLButtonElement = fixture.nativeElement.querySelector('button[type="submit"]');
    expect(submit.disabled).toBeTrue();

    fixture.componentInstance.temperatureValue.set(180);
    fixture.componentInstance.setFromScale('Celsius');
    await fixture.componentInstance.convert();
    http.expectNone(CONVERT_URL);
  });

  it('offers a retry when the conversion could not be reached', async () => {
    const harness = await createFixture();
    harness.fixture.componentInstance.temperatureValue.set(180);
    harness.fixture.componentInstance.setFromScale('Celsius');
    await convert(harness, (req) => req.flush({}, { status: 500, statusText: 'Server Error' }));

    expect(text(harness.fixture)).toContain('try again');
  });

  // ---- Applying ----

  async function converted(options: { editorIsDirty?: boolean } = {}): Promise<Harness> {
    const harness = await createFixture();
    if (options.editorIsDirty) harness.fixture.componentRef.setInput('editorIsDirty', true);

    harness.fixture.componentInstance.prefillFrom('step-2');
    harness.fixture.detectChanges();
    await convert(harness, (req) => req.flush(conversionPayload()));
    return harness;
  }

  it('offers to write a conversion back to the step it came from', async () => {
    const harness = await converted();

    expect(harness.fixture.componentInstance.canApply()).toBeTrue();
    expect(harness.fixture.componentInstance.applyBlockedReason()).toBeNull();
    expect(text(harness.fixture)).toContain('What would change');
    expect(text(harness.fixture)).toContain('180 °C');
    expect(text(harness.fixture)).toContain('356 °F');
  });

  it('raises the step, the converted value and the target unit', async () => {
    const harness = await converted();
    const raised: unknown[] = [];
    harness.fixture.componentInstance.applyRequested.subscribe((event) => raised.push(event));

    harness.fixture.componentInstance.requestApply();

    expect(raised).toEqual([
      jasmine.objectContaining({
        stepId: 'step-2',
        temperatureValue: 356,
        temperatureUnitId: 'unit-fahrenheit',
        beforeLabel: '180 °C',
        afterLabel: '356 °F',
      }),
    ]);
  });

  // Writing a hand-typed number onto a step would record something the recipe never said.
  it('will not write back a number that did not come from a step', async () => {
    const harness = await createFixture();
    harness.fixture.componentInstance.temperatureValue.set(200);
    harness.fixture.componentInstance.setFromScale('Celsius');
    await convert(harness, (req) => req.flush(conversionPayload()));

    expect(harness.fixture.componentInstance.canApply()).toBeFalse();
    expect(harness.fixture.componentInstance.applyBlockedReason()).toContain('a number you typed');
  });

  // The link to the step is broken deliberately once the inputs stop matching it.
  it('will not write back once the value has been edited away from the step', async () => {
    const harness = await converted();
    harness.fixture.componentInstance.temperatureValue.set(200);
    harness.fixture.detectChanges();

    expect(harness.fixture.componentInstance.canApply()).toBeFalse();
    expect(harness.fixture.componentInstance.applyBlockedReason()).toContain('a number you typed');
  });

  it('will not write back while the editor has unsaved changes, and says why', async () => {
    const harness = await converted({ editorIsDirty: true });

    expect(harness.fixture.componentInstance.canApply()).toBeFalse();
    expect(text(harness.fixture)).toContain('Save or discard your changes first');
  });

  it('raises nothing when the control is asked while blocked', async () => {
    const harness = await converted({ editorIsDirty: true });
    const raised: unknown[] = [];
    harness.fixture.componentInstance.applyRequested.subscribe((event) => raised.push(event));

    harness.fixture.componentInstance.requestApply();

    expect(raised).toEqual([]);
  });

  // The source-version match the RESTRICTION asks for: a result computed against a version the recipe has
  // moved past is discarded outright rather than offered.
  it('drops the result and the step link when the recipe moves to another version', async () => {
    const harness = await converted();

    harness.fixture.componentRef.setInput('sourceVersionNumber', 4);
    harness.fixture.detectChanges();

    expect(harness.fixture.componentInstance.result()).toBeNull();
    expect(harness.fixture.componentInstance.canApply()).toBeFalse();
  });

  // ---- Accessibility ----

  it('names each scale chooser with its own legend', async () => {
    const { fixture } = await createFixture();
    const legends: HTMLLegendElement[] = Array.from(fixture.nativeElement.querySelectorAll('fieldset legend'));

    expect(legends.map((legend) => legend.textContent?.trim())).toEqual([
      'This temperature is in',
      'Convert it to',
      'Carried through untouched — not used in the calculation',
    ]);
  });

  it('announces a landed result politely, without moving focus', async () => {
    const harness = await createFixture();
    harness.fixture.componentInstance.temperatureValue.set(180);
    harness.fixture.componentInstance.setFromScale('Celsius');
    await convert(harness, (req) => req.flush(conversionPayload()));

    // The announcement is a persistent region whose text changes, not the card itself: a live region
    // inserted already holding its content is not reliably announced.
    const region: HTMLElement = harness.fixture.nativeElement.querySelector('[role="status"][aria-live="polite"]');
    expect(region.getAttribute('aria-live')).toBe('polite');
    expect(region.textContent?.trim().length).toBeGreaterThan(0);
    expect(harness.fixture.nativeElement.querySelector('.result-card').getAttribute('role')).toBeNull();
  });

  it('pairs the caution glyph with its own words', async () => {
    const harness = await createFixture();
    harness.fixture.componentInstance.temperatureValue.set(180);
    harness.fixture.componentInstance.setFromScale('Celsius');
    harness.fixture.componentInstance.safetyNote.set('until the centre reads 74 °C');
    await convert(harness, (req) =>
      req.flush(conversionPayload({ safetyNote: 'until the centre reads 74 °C' })),
    );

    const glyph: HTMLElement = harness.fixture.nativeElement.querySelector('.caution-glyph');
    expect(glyph.getAttribute('aria-hidden')).toBe('true');
    expect((glyph.parentElement?.textContent ?? '').trim().length).toBeGreaterThan(20);
  });

  it('marks the refused precision field invalid', async () => {
    const harness = await createFixture();
    harness.fixture.componentInstance.temperatureValue.set(180);
    harness.fixture.componentInstance.setFromScale('Celsius');
    harness.fixture.componentInstance.precisionValue.set(-1);
    await convert(harness, (req) =>
      req.flush(
        {
          code: 'recipes.temperature-conversion.invalid_request',
          errors: { precision: ['Precision cannot be negative.'] },
        },
        { status: 400, statusText: 'Bad Request' },
      ),
    );

    const input: HTMLInputElement = harness.fixture.nativeElement.querySelector(
      `#${harness.fixture.componentInstance.precisionFieldId}`,
    );
    expect(input.getAttribute('aria-invalid')).toBe('true');
  });
});
