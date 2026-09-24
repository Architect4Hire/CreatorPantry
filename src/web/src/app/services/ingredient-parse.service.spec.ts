import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { RuntimeConfigService } from '../core/runtime-config.service';
import { ParsedIngredientLine } from '../models/ingredient-parse.models';
import { IngredientParseService } from './ingredient-parse.service';

const PARSED_LINE: ParsedIngredientLine = {
  tokens: {
    originalText: '2 tsp kosher salt',
    isGroupMarker: false,
    quantity: { span: { start: 0, length: 1, text: '2' }, value: { numerator: '2', denominator: '1' }, range: null, isInvalid: false },
    packageQuantity: null,
    unitCandidate: { start: 2, length: 3, text: 'tsp' },
    ingredientText: { start: 6, length: 11, text: 'kosher salt' },
    preparationText: [],
    isOptional: false,
    ambiguities: [],
  },
  ingredientMatch: {
    inputText: 'kosher salt',
    resolved: { ingredientId: 'ing1', canonicalName: 'kosher salt', kind: 'CanonicalName' },
    alternates: [],
    isAmbiguous: false,
  },
  unitMatch: {
    inputText: 'tsp',
    resolved: { measurementUnitId: 'unit1', displayName: 'teaspoon', kind: 'Code' },
    alternates: [],
    isAmbiguous: false,
  },
};

describe('IngredientParseService', () => {
  let service: IngredientParseService;
  let http: HttpTestingController;

  beforeEach(async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);

    const runtimeConfig = TestBed.inject(RuntimeConfigService);
    const loading = runtimeConfig.load();
    http.expectOne('/runtime-config.json').flush({ gatewayUrl: 'https://gateway.example' });
    await loading;

    service = TestBed.inject(IngredientParseService);
  });

  afterEach(() => http.verify());

  it('POSTs the lines with credentials and decodes a 200 response', async () => {
    const call = service.parseIngredientLines('cozy-fall', ['2 tsp kosher salt']);
    const req = http.expectOne('https://gateway.example/api/v1/workspaces/cozy-fall/ingredient-tools/parse');

    expect(req.request.method).toBe('POST');
    expect(req.request.withCredentials).toBeTrue();
    expect(req.request.body).toEqual({ lines: ['2 tsp kosher salt'] });

    req.flush({ lines: [PARSED_LINE] });

    expect(await call).toEqual({ status: 'parsed', lines: [PARSED_LINE] });
  });

  it('encodes the workspace slug into the URL', async () => {
    const call = service.parseIngredientLines('sam & co', ['2 cups flour']);
    const req = http.expectOne('https://gateway.example/api/v1/workspaces/sam%20%26%20co/ingredient-tools/parse');
    req.flush({ lines: [] });
    await call;
  });

  it('reports a 400 as validation_failed with the decoded field errors', async () => {
    const call = service.parseIngredientLines('cozy-fall', []);
    const req = http.expectOne('https://gateway.example/api/v1/workspaces/cozy-fall/ingredient-tools/parse');

    req.flush(
      { code: 'recipes.ingredient-lines.invalid_request', errors: { lines: ['At least one line is required.'] } },
      { status: 400, statusText: 'Bad Request' },
    );

    expect(await call).toEqual({ status: 'validation_failed', fieldErrors: { lines: ['At least one line is required.'] } });
  });

  it('reports every other failure as unavailable', async () => {
    const call = service.parseIngredientLines('cozy-fall', ['2 cups flour']);
    const req = http.expectOne('https://gateway.example/api/v1/workspaces/cozy-fall/ingredient-tools/parse');
    req.flush({}, { status: 500, statusText: 'Server Error' });

    expect(await call).toEqual({ status: 'unavailable' });
  });

  it('reports unavailable when the response body does not decode', async () => {
    const call = service.parseIngredientLines('cozy-fall', ['2 cups flour']);
    const req = http.expectOne('https://gateway.example/api/v1/workspaces/cozy-fall/ingredient-tools/parse');
    req.flush({ lines: [{ tokens: 'not an object' }] });

    expect(await call).toEqual({ status: 'unavailable' });
  });
});
