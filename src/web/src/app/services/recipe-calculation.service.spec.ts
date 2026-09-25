import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { RuntimeConfigService } from '../core/runtime-config.service';
import { RecipeCalculationService } from './recipe-calculation.service';

const RECIPE_ID = '2a1b7c6d-0000-4000-8000-000000000001';
const SCALE_URL = `https://gateway.example/api/v1/workspaces/cozy-fall/recipes/${RECIPE_ID}/calculations/scale`;

function scalingPayload(): Record<string, unknown> {
  return {
    sourceVersionNumber: 3,
    preview: {
      factor: { numerator: '2', denominator: '1' },
      requestedTargetYieldQuantity: null,
      scaledYieldQuantity: null,
      scaledYieldDisplayQuantity: null,
      lines: [
        {
          id: 'line-1',
          displayText: '250 g flour',
          wasScaled: true,
          scaledQuantity: { numerator: '500', denominator: '1' },
          scaledQuantityUpper: null,
          scaledDisplayQuantity: 500,
          scaledDisplayQuantityUpper: null,
          warnings: [],
        },
      ],
      recipeWarnings: ['RecipeYieldNotStructured'],
    },
  };
}

describe('RecipeCalculationService', () => {
  let service: RecipeCalculationService;
  let http: HttpTestingController;

  beforeEach(async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);

    const runtimeConfig = TestBed.inject(RuntimeConfigService);
    const loading = runtimeConfig.load();
    http.expectOne('/runtime-config.json').flush({ gatewayUrl: 'https://gateway.example' });
    await loading;

    service = TestBed.inject(RecipeCalculationService);
  });

  afterEach(() => http.verify());

  it('POSTs a multiplier with credentials and decodes a 200 response', async () => {
    const call = service.scaleRecipe('cozy-fall', RECIPE_ID, { sourceVersionNumber: 3, multiplier: 2 });
    const req = http.expectOne(SCALE_URL);

    expect(req.request.method).toBe('POST');
    expect(req.request.withCredentials).toBeTrue();
    expect(req.request.body).toEqual({ sourceVersionNumber: 3, multiplier: 2 });

    req.flush(scalingPayload());

    const outcome = await call;
    expect(outcome.status).toBe('scaled');
    expect(outcome.status === 'scaled' && outcome.result.sourceVersionNumber).toBe(3);
  });

  it('POSTs a target yield without naming the multiplier', async () => {
    const call = service.scaleRecipe('cozy-fall', RECIPE_ID, { sourceVersionNumber: 3, targetYieldQuantity: 24 });
    const req = http.expectOne(SCALE_URL);

    expect(req.request.body).toEqual({ sourceVersionNumber: 3, targetYieldQuantity: 24 });

    req.flush(scalingPayload());
    await call;
  });

  it('encodes the workspace slug and recipe id into the URL', async () => {
    const call = service.scaleRecipe('sam & co', RECIPE_ID, { sourceVersionNumber: 1, multiplier: 2 });
    const req = http.expectOne(
      `https://gateway.example/api/v1/workspaces/sam%20%26%20co/recipes/${RECIPE_ID}/calculations/scale`,
    );

    req.flush(scalingPayload());
    await call;
  });

  // A body this client cannot read is not a preview. Rendering a partially understood calculation would be
  // worse than saying nothing happened.
  it('treats an undecodable 200 body as unavailable', async () => {
    const call = service.scaleRecipe('cozy-fall', RECIPE_ID, { sourceVersionNumber: 3, multiplier: 2 });
    http.expectOne(SCALE_URL).flush({ sourceVersionNumber: 3, preview: { factor: 'two' } });

    expect(await call).toEqual({ status: 'unavailable' });
  });

  it('maps a 400 to invalid_request, carrying the field the server named', async () => {
    const call = service.scaleRecipe('cozy-fall', RECIPE_ID, { sourceVersionNumber: 3, multiplier: 0 });
    http.expectOne(SCALE_URL).flush(
      {
        code: 'recipes.scaling.invalid_request',
        errors: { multiplier: ['The multiplier must be greater than zero.'] },
      },
      { status: 400, statusText: 'Bad Request' },
    );

    expect(await call).toEqual({
      status: 'invalid_request',
      fieldErrors: { multiplier: ['The multiplier must be greater than zero.'] },
    });
  });

  it('maps a 400 naming the target yield to invalid_request', async () => {
    const call = service.scaleRecipe('cozy-fall', RECIPE_ID, { sourceVersionNumber: 3, targetYieldQuantity: 24 });
    http.expectOne(SCALE_URL).flush(
      {
        code: 'recipes.scaling.invalid_request',
        errors: { targetYieldQuantity: ['This recipe has no structured yield to scale toward.'] },
      },
      { status: 400, statusText: 'Bad Request' },
    );

    const outcome = await call;
    expect(outcome.status).toBe('invalid_request');
    expect(outcome.status === 'invalid_request' && outcome.fieldErrors['targetYieldQuantity']).toEqual([
      'This recipe has no structured yield to scale toward.',
    ]);
  });

  // Both are 404 and the stable code is the only thing that tells them apart.
  it('distinguishes a missing version from an unreadable recipe on a 404', async () => {
    const versionCall = service.scaleRecipe('cozy-fall', RECIPE_ID, { sourceVersionNumber: 9, multiplier: 2 });
    http.expectOne(SCALE_URL).flush(
      { code: 'recipes.version.not_found', errors: { sourceVersionNumber: ['This recipe has no version 9.'] } },
      { status: 404, statusText: 'Not Found' },
    );

    expect(await versionCall).toEqual({
      status: 'version_not_found',
      fieldErrors: { sourceVersionNumber: ['This recipe has no version 9.'] },
    });

    const recipeCall = service.scaleRecipe('cozy-fall', RECIPE_ID, { sourceVersionNumber: 1, multiplier: 2 });
    http.expectOne(SCALE_URL).flush(
      { code: 'recipes.recipe.not_found' },
      { status: 404, statusText: 'Not Found' },
    );

    expect(await recipeCall).toEqual({ status: 'not_found' });
  });

  it('maps a server failure to unavailable', async () => {
    const call = service.scaleRecipe('cozy-fall', RECIPE_ID, { sourceVersionNumber: 3, multiplier: 2 });
    http.expectOne(SCALE_URL).flush({}, { status: 500, statusText: 'Server Error' });

    expect(await call).toEqual({ status: 'unavailable' });
  });
});

const CONVERT_UNITS_URL = `https://gateway.example/api/v1/workspaces/cozy-fall/recipes/${RECIPE_ID}/calculations/convert-units`;
const CONVERT_TEMPERATURE_URL = `https://gateway.example/api/v1/workspaces/cozy-fall/recipes/${RECIPE_ID}/calculations/convert-temperature`;

function unitConversionPayload(): Record<string, unknown> {
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
    },
  };
}

function temperatureConversionPayload(): Record<string, unknown> {
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
    },
  };
}

describe('RecipeCalculationService unit conversion', () => {
  let service: RecipeCalculationService;
  let http: HttpTestingController;

  beforeEach(async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);

    const runtimeConfig = TestBed.inject(RuntimeConfigService);
    const loading = runtimeConfig.load();
    http.expectOne('/runtime-config.json').flush({ gatewayUrl: 'https://gateway.example' });
    await loading;

    service = TestBed.inject(RecipeCalculationService);
  });

  afterEach(() => http.verify());

  it('POSTs the quantity and both units with credentials, and decodes a 200', async () => {
    const call = service.convertUnits('cozy-fall', RECIPE_ID, {
      sourceVersionNumber: 3,
      quantity: 1000,
      fromUnitId: 'unit-gram',
      toUnitId: 'unit-kilogram',
    });
    const req = http.expectOne(CONVERT_UNITS_URL);

    expect(req.request.method).toBe('POST');
    expect(req.request.withCredentials).toBeTrue();
    expect(req.request.body).toEqual({
      sourceVersionNumber: 3,
      quantity: 1000,
      fromUnitId: 'unit-gram',
      toUnitId: 'unit-kilogram',
    });

    req.flush(unitConversionPayload());

    const outcome = await call;
    expect(outcome.status).toBe('converted');
    expect(outcome.status === 'converted' && outcome.result.result.convertedDisplayQuantity).toBe(0.25);
  });

  // Every pair of units that cannot be bridged arrives this way. None of them is a partial success (ING-004).
  it('maps incompatible units to invalid_request naming toUnitId', async () => {
    const call = service.convertUnits('cozy-fall', RECIPE_ID, {
      sourceVersionNumber: 3,
      quantity: 2,
      fromUnitId: 'unit-bunch',
      toUnitId: 'unit-clove',
    });
    http.expectOne(CONVERT_UNITS_URL).flush(
      {
        code: 'recipes.unit-conversion.invalid_request',
        errors: { toUnitId: ['These units cannot be converted between each other.'] },
      },
      { status: 400, statusText: 'Bad Request' },
    );

    const outcome = await call;
    expect(outcome.status).toBe('invalid_request');
    expect(outcome.status === 'invalid_request' && outcome.fieldErrors['toUnitId']).toEqual([
      'These units cannot be converted between each other.',
    ]);
  });

  it('maps a missing density to invalid_request naming toUnitId', async () => {
    const call = service.convertUnits('cozy-fall', RECIPE_ID, {
      sourceVersionNumber: 3,
      quantity: 2,
      fromUnitId: 'unit-cup',
      toUnitId: 'unit-gram',
    });
    http.expectOne(CONVERT_UNITS_URL).flush(
      {
        code: 'recipes.unit-conversion.invalid_request',
        errors: {
          toUnitId: ['This conversion needs an ingredient-specific density reference, which is not yet available.'],
        },
      },
      { status: 400, statusText: 'Bad Request' },
    );

    expect((await call).status).toBe('invalid_request');
  });

  it('maps a temperature unit to invalid_request rather than converting it here', async () => {
    const call = service.convertUnits('cozy-fall', RECIPE_ID, {
      sourceVersionNumber: 3,
      quantity: 180,
      fromUnitId: 'unit-celsius',
      toUnitId: 'unit-fahrenheit',
    });
    http.expectOne(CONVERT_UNITS_URL).flush(
      {
        code: 'recipes.unit-conversion.invalid_request',
        errors: { toUnitId: ['Temperature units use the temperature calculation instead.'] },
      },
      { status: 400, statusText: 'Bad Request' },
    );

    const outcome = await call;
    expect(outcome.status === 'invalid_request' && outcome.fieldErrors['toUnitId']).toEqual([
      'Temperature units use the temperature calculation instead.',
    ]);
  });

  it('treats an undecodable 200 body as unavailable', async () => {
    const call = service.convertUnits('cozy-fall', RECIPE_ID, {
      sourceVersionNumber: 3,
      quantity: 1,
      fromUnitId: 'unit-gram',
      toUnitId: 'unit-kilogram',
    });
    http.expectOne(CONVERT_UNITS_URL).flush({ sourceVersionNumber: 3, result: { method: 'Guesswork' } });

    expect(await call).toEqual({ status: 'unavailable' });
  });

  it('distinguishes a missing version from an unreadable recipe on a 404', async () => {
    const versionCall = service.convertUnits('cozy-fall', RECIPE_ID, {
      sourceVersionNumber: 9,
      quantity: 1,
      fromUnitId: 'unit-gram',
      toUnitId: 'unit-kilogram',
    });
    http.expectOne(CONVERT_UNITS_URL).flush(
      { code: 'recipes.version.not_found', errors: { sourceVersionNumber: ['This recipe has no version 9.'] } },
      { status: 404, statusText: 'Not Found' },
    );
    expect((await versionCall).status).toBe('version_not_found');

    const recipeCall = service.convertUnits('cozy-fall', RECIPE_ID, {
      sourceVersionNumber: 1,
      quantity: 1,
      fromUnitId: 'unit-gram',
      toUnitId: 'unit-kilogram',
    });
    http.expectOne(CONVERT_UNITS_URL).flush(
      { code: 'recipes.recipe.not_found' },
      { status: 404, statusText: 'Not Found' },
    );
    expect(await recipeCall).toEqual({ status: 'not_found' });
  });
});

describe('RecipeCalculationService temperature conversion', () => {
  let service: RecipeCalculationService;
  let http: HttpTestingController;

  beforeEach(async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);

    const runtimeConfig = TestBed.inject(RuntimeConfigService);
    const loading = runtimeConfig.load();
    http.expectOne('/runtime-config.json').flush({ gatewayUrl: 'https://gateway.example' });
    await loading;

    service = TestBed.inject(RecipeCalculationService);
  });

  afterEach(() => http.verify());

  it('POSTs the value, both scales, the precision and both pass-through fields', async () => {
    const call = service.convertTemperature('cozy-fall', RECIPE_ID, {
      sourceVersionNumber: 3,
      value: 180,
      fromScale: 'Celsius',
      toScale: 'Fahrenheit',
      precision: 0,
      ovenModeContext: 'convection',
      safetyNote: 'until the centre reads 74 °C',
    });
    const req = http.expectOne(CONVERT_TEMPERATURE_URL);

    expect(req.request.body).toEqual({
      sourceVersionNumber: 3,
      value: 180,
      fromScale: 'Celsius',
      toScale: 'Fahrenheit',
      precision: 0,
      ovenModeContext: 'convection',
      safetyNote: 'until the centre reads 74 °C',
    });

    req.flush(temperatureConversionPayload());
    expect((await call).status).toBe('converted');
  });

  // Temperatures go below zero; nothing about a negative one is a refusal.
  it('sends a negative temperature unchanged', async () => {
    const call = service.convertTemperature('cozy-fall', RECIPE_ID, {
      sourceVersionNumber: 3,
      value: -18,
      fromScale: 'Celsius',
      toScale: 'Fahrenheit',
      precision: 0,
      ovenModeContext: null,
      safetyNote: null,
    });
    const req = http.expectOne(CONVERT_TEMPERATURE_URL);

    expect(req.request.body).toEqual(jasmine.objectContaining({ value: -18 }));
    req.flush(temperatureConversionPayload());
    await call;
  });

  it('maps a negative precision to invalid_request naming precision', async () => {
    const call = service.convertTemperature('cozy-fall', RECIPE_ID, {
      sourceVersionNumber: 3,
      value: 180,
      fromScale: 'Celsius',
      toScale: 'Fahrenheit',
      precision: -1,
      ovenModeContext: null,
      safetyNote: null,
    });
    http.expectOne(CONVERT_TEMPERATURE_URL).flush(
      {
        code: 'recipes.temperature-conversion.invalid_request',
        errors: { precision: ['Precision cannot be negative.'] },
      },
      { status: 400, statusText: 'Bad Request' },
    );

    const outcome = await call;
    expect(outcome.status === 'invalid_request' && outcome.fieldErrors['precision']).toEqual([
      'Precision cannot be negative.',
    ]);
  });

  it('maps a server failure to unavailable', async () => {
    const call = service.convertTemperature('cozy-fall', RECIPE_ID, {
      sourceVersionNumber: 3,
      value: 180,
      fromScale: 'Celsius',
      toScale: 'Fahrenheit',
      precision: 0,
      ovenModeContext: null,
      safetyNote: null,
    });
    http.expectOne(CONVERT_TEMPERATURE_URL).flush({}, { status: 500, statusText: 'Server Error' });

    expect(await call).toEqual({ status: 'unavailable' });
  });
});

const YIELD_URL = `https://gateway.example/api/v1/workspaces/cozy-fall/recipes/${RECIPE_ID}/calculations/recalculate-yield`;
const DISPLAY_URL = `https://gateway.example/api/v1/workspaces/cozy-fall/recipes/${RECIPE_ID}/calculations/normalize-display`;

function yieldPayload(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    sourceVersionNumber: 3,
    preview: {
      status: 'Reconciled',
      batchYield: 12,
      servingCount: 6,
      servingSize: 2,
      solvedField: null,
      formula: 'servingCount × servingSize',
      panVolume: null,
      panFillRatio: null,
      panComparable: null,
      ...overrides,
    },
  };
}

function displayPayload(overrides: Record<string, unknown> = {}): Record<string, unknown> {
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

describe('RecipeCalculationService yield reconciliation', () => {
  let service: RecipeCalculationService;
  let http: HttpTestingController;

  beforeEach(async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);

    const runtimeConfig = TestBed.inject(RuntimeConfigService);
    const loading = runtimeConfig.load();
    http.expectOne('/runtime-config.json').flush({ gatewayUrl: 'https://gateway.example' });
    await loading;

    service = TestBed.inject(RecipeCalculationService);
  });

  afterEach(() => http.verify());

  it('POSTs every entered value with credentials and decodes a 200', async () => {
    const call = service.recalculateYield('cozy-fall', RECIPE_ID, {
      sourceVersionNumber: 3,
      batchYield: 12,
      servingCount: 6,
      servingSize: 2,
      panVolume: 9,
      displayPrecision: 2,
    });
    const req = http.expectOne(YIELD_URL);

    expect(req.request.method).toBe('POST');
    expect(req.request.withCredentials).toBeTrue();
    expect(req.request.body).toEqual({
      sourceVersionNumber: 3,
      batchYield: 12,
      servingCount: 6,
      servingSize: 2,
      panVolume: 9,
      displayPrecision: 2,
    });

    req.flush(yieldPayload());

    const outcome = await call;
    expect(outcome.status).toBe('reconciled');
    expect(outcome.status === 'reconciled' && outcome.result.preview.status).toBe('Reconciled');
  });

  it('omits the values left blank rather than sending them as null', async () => {
    const call = service.recalculateYield('cozy-fall', RECIPE_ID, {
      sourceVersionNumber: 3,
      batchYield: null,
      servingCount: 6,
      servingSize: 2,
      panVolume: null,
      displayPrecision: null,
    });
    const req = http.expectOne(YIELD_URL);

    expect(req.request.body).toEqual({ sourceVersionNumber: 3, servingCount: 6, servingSize: 2 });

    req.flush(yieldPayload({ status: 'Solved', solvedField: 'BatchYield' }));
    await call;
  });

  // Neither of these is a refusal — both are conclusions the route answers 200 with.
  it('treats too few values as a successful answer carrying a status', async () => {
    const call = service.recalculateYield('cozy-fall', RECIPE_ID, {
      sourceVersionNumber: 3,
      batchYield: 12,
      servingCount: null,
      servingSize: null,
      panVolume: null,
      displayPrecision: null,
    });
    http.expectOne(YIELD_URL).flush(
      yieldPayload({ status: 'InsufficientInput', servingCount: null, servingSize: null, formula: null }),
    );

    const outcome = await call;
    expect(outcome.status).toBe('reconciled');
    expect(outcome.status === 'reconciled' && outcome.result.preview.status).toBe('InsufficientInput');
  });

  it('treats three values that disagree as a successful answer carrying a status', async () => {
    const call = service.recalculateYield('cozy-fall', RECIPE_ID, {
      sourceVersionNumber: 3,
      batchYield: 99,
      servingCount: 6,
      servingSize: 2,
      panVolume: null,
      displayPrecision: null,
    });
    http.expectOne(YIELD_URL).flush(yieldPayload({ status: 'Contradictory', batchYield: 99 }));

    const outcome = await call;
    expect(outcome.status === 'reconciled' && outcome.result.preview.status).toBe('Contradictory');
  });

  it('maps a non-positive value to invalid_request naming the field at fault', async () => {
    const call = service.recalculateYield('cozy-fall', RECIPE_ID, {
      sourceVersionNumber: 3,
      batchYield: 0,
      servingCount: 6,
      servingSize: 2,
      panVolume: null,
      displayPrecision: null,
    });
    http.expectOne(YIELD_URL).flush(
      {
        code: 'recipes.yield-recalculation.invalid_request',
        errors: { batchYield: ['The batch yield must be greater than zero.'] },
      },
      { status: 400, statusText: 'Bad Request' },
    );

    const outcome = await call;
    expect(outcome.status === 'invalid_request' && outcome.fieldErrors['batchYield']).toEqual([
      'The batch yield must be greater than zero.',
    ]);
  });

  it('treats an undecodable 200 body as unavailable', async () => {
    const call = service.recalculateYield('cozy-fall', RECIPE_ID, {
      sourceVersionNumber: 3,
      batchYield: 12,
      servingCount: 6,
      servingSize: 2,
      panVolume: null,
      displayPrecision: null,
    });
    http.expectOne(YIELD_URL).flush(yieldPayload({ status: 'Approximated' }));

    expect(await call).toEqual({ status: 'unavailable' });
  });

  it('distinguishes a missing version from an unreadable recipe on a 404', async () => {
    const request = {
      sourceVersionNumber: 9,
      batchYield: 12,
      servingCount: 6,
      servingSize: 2,
      panVolume: null,
      displayPrecision: null,
    };

    const versionCall = service.recalculateYield('cozy-fall', RECIPE_ID, request);
    http.expectOne(YIELD_URL).flush(
      { code: 'recipes.version.not_found', errors: { sourceVersionNumber: ['This recipe has no version 9.'] } },
      { status: 404, statusText: 'Not Found' },
    );
    expect((await versionCall).status).toBe('version_not_found');

    const recipeCall = service.recalculateYield('cozy-fall', RECIPE_ID, request);
    http.expectOne(YIELD_URL).flush({ code: 'recipes.recipe.not_found' }, { status: 404, statusText: 'Not Found' });
    expect(await recipeCall).toEqual({ status: 'not_found' });
  });
});

describe('RecipeCalculationService display normalization', () => {
  let service: RecipeCalculationService;
  let http: HttpTestingController;

  beforeEach(async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);

    const runtimeConfig = TestBed.inject(RuntimeConfigService);
    const loading = runtimeConfig.load();
    http.expectOne('/runtime-config.json').flush({ gatewayUrl: 'https://gateway.example' });
    await loading;

    service = TestBed.inject(RecipeCalculationService);
  });

  afterEach(() => http.verify());

  it('POSTs the value, unit and presentation choices, and decodes a 200', async () => {
    const call = service.normalizeDisplay('cozy-fall', RECIPE_ID, {
      sourceVersionNumber: 3,
      value: 1.5,
      upperValue: null,
      unitId: 'unit-cup',
      precision: 2,
      useAbbreviation: false,
      rounding: 'ToEven',
    });
    const req = http.expectOne(DISPLAY_URL);

    expect(req.request.method).toBe('POST');
    expect(req.request.withCredentials).toBeTrue();
    expect(req.request.body).toEqual({
      sourceVersionNumber: 3,
      value: 1.5,
      unitId: 'unit-cup',
      precision: 2,
      useAbbreviation: false,
      rounding: 'ToEven',
    });

    req.flush(displayPayload());

    const outcome = await call;
    expect(outcome.status).toBe('rendered');
    expect(outcome.status === 'rendered' && outcome.result.result.text).toBe('1½ cups');
  });

  it('names the upper value only for a range', async () => {
    const call = service.normalizeDisplay('cozy-fall', RECIPE_ID, {
      sourceVersionNumber: 3,
      value: 1,
      upperValue: 1.5,
      unitId: 'unit-cup',
      precision: 2,
      useAbbreviation: false,
      rounding: 'ToEven',
    });
    const req = http.expectOne(DISPLAY_URL);

    expect(req.request.body).toEqual(jasmine.objectContaining({ upperValue: 1.5 }));

    req.flush(displayPayload({ text: '1–1½ cups', presentation: 'Whole', upperPresentation: 'Fraction' }));

    const outcome = await call;
    expect(outcome.status === 'rendered' && outcome.result.result.upperPresentation).toBe('Fraction');
  });

  it('sends the chosen rounding mode rather than a default', async () => {
    const call = service.normalizeDisplay('cozy-fall', RECIPE_ID, {
      sourceVersionNumber: 3,
      value: 1.025,
      upperValue: null,
      unitId: 'unit-cup',
      precision: 2,
      useAbbreviation: true,
      rounding: 'AwayFromZero',
    });
    const req = http.expectOne(DISPLAY_URL);

    expect(req.request.body).toEqual(
      jasmine.objectContaining({ rounding: 'AwayFromZero', useAbbreviation: true }),
    );

    req.flush(displayPayload({ text: '1.03 c', presentation: 'Decimal', rounding: 'AwayFromZero' }));
    await call;
  });

  it('maps an upper value that is not above the value to invalid_request', async () => {
    const call = service.normalizeDisplay('cozy-fall', RECIPE_ID, {
      sourceVersionNumber: 3,
      value: 2,
      upperValue: 2,
      unitId: 'unit-cup',
      precision: 2,
      useAbbreviation: false,
      rounding: 'ToEven',
    });
    http.expectOne(DISPLAY_URL).flush(
      {
        code: 'recipes.display-normalization.invalid_request',
        errors: { upperValue: ['The upper value must be greater than the value.'] },
      },
      { status: 400, statusText: 'Bad Request' },
    );

    const outcome = await call;
    expect(outcome.status === 'invalid_request' && outcome.fieldErrors['upperValue']).toEqual([
      'The upper value must be greater than the value.',
    ]);
  });

  it('maps a unit that is not available to invalid_request naming unitId', async () => {
    const call = service.normalizeDisplay('cozy-fall', RECIPE_ID, {
      sourceVersionNumber: 3,
      value: 1.5,
      upperValue: null,
      unitId: 'unit-gone',
      precision: 2,
      useAbbreviation: false,
      rounding: 'ToEven',
    });
    http.expectOne(DISPLAY_URL).flush(
      {
        code: 'recipes.display-normalization.invalid_request',
        errors: { unitId: ['That unit is not available.'] },
      },
      { status: 400, statusText: 'Bad Request' },
    );

    expect((await call).status).toBe('invalid_request');
  });

  it('maps a server failure to unavailable', async () => {
    const call = service.normalizeDisplay('cozy-fall', RECIPE_ID, {
      sourceVersionNumber: 3,
      value: 1.5,
      upperValue: null,
      unitId: 'unit-cup',
      precision: 2,
      useAbbreviation: false,
      rounding: 'ToEven',
    });
    http.expectOne(DISPLAY_URL).flush({}, { status: 500, statusText: 'Server Error' });

    expect(await call).toEqual({ status: 'unavailable' });
  });
});

describe('RecipeCalculationService without a resolved gateway', () => {
  it('answers unavailable rather than calling a relative URL', async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    const http = TestBed.inject(HttpTestingController);
    const service = TestBed.inject(RecipeCalculationService);

    expect(await service.scaleRecipe('cozy-fall', RECIPE_ID, { sourceVersionNumber: 1, multiplier: 2 })).toEqual({
      status: 'unavailable',
    });

    http.verify();
  });
});
