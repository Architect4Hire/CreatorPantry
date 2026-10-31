import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { RuntimeConfigService } from '../core/runtime-config.service';
import { DietaryAllergenAnalysisOutcome, DietaryAllergenAnalysisService } from './dietary-allergen-analysis.service';

const RECIPE_ID = '2a1b7c6d-0000-4000-8000-000000000001';
const URL =
  `https://gateway.example/api/v1/workspaces/cozy-fall/recipes/${RECIPE_ID}` + '/versions/4/analysis/dietary-allergen';

function analysisPayload(): Record<string, unknown> {
  return {
    recipeId: RECIPE_ID,
    recipeVersionId: 'v4',
    versionNumber: 4,
    methodVersion: 'dietary-allergen/1',
    evaluatedOn: '2026-10-20',
    crossContactDisclaimer: 'This analysis reads reviewed ingredient records only.',
    allergens: [
      {
        allergenId: 'a1',
        allergenName: 'Milk',
        finding: 'Unknown',
        confidence: 'None',
        coverage: { linesRead: 2, linesWithCountedRecords: 0 },
        statement: 'Not known whether this recipe contains Milk: at least one ingredient has no reviewed record.',
        containingLines: [],
        possibleSources: [],
        unknownLines: ['l1'],
        conflictingLines: [],
        lines: [
          {
            lineId: 'l1',
            displayText: '2 eggs',
            isOptional: false,
            ingredientId: 'i1',
            finding: 'Unknown',
            reason: 'NoRecord',
            conflict: false,
            evidence: [],
          },
        ],
      },
    ],
    dietary: [],
    sources: [],
  };
}

describe('DietaryAllergenAnalysisService', () => {
  let service: DietaryAllergenAnalysisService;
  let http: HttpTestingController;

  beforeEach(async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);

    const runtimeConfig = TestBed.inject(RuntimeConfigService);
    const loading = runtimeConfig.load();
    http.expectOne('/runtime-config.json').flush({ gatewayUrl: 'https://gateway.example' });
    await loading;

    service = TestBed.inject(DietaryAllergenAnalysisService);
  });

  afterEach(() => http.verify());

  function analyse(): Promise<DietaryAllergenAnalysisOutcome> {
    return service.analyse('cozy-fall', RECIPE_ID, 4, { allergenIds: ['a1'], dietaryProfileIds: [] });
  }

  it('POSTs the selection to the named version, with credentials, and decodes a 200', async () => {
    const call = service.analyse('cozy-fall', RECIPE_ID, 4, {
      allergenIds: ['a1', 'a2'],
      dietaryProfileIds: ['d1'],
    });
    const req = http.expectOne(URL);

    expect(req.request.method).toBe('POST');
    expect(req.request.withCredentials).toBeTrue();
    expect(req.request.body).toEqual({ allergenIds: ['a1', 'a2'], dietaryProfileIds: ['d1'] });

    req.flush(analysisPayload());

    const outcome = await call;
    expect(outcome.status).toBe('analysed');
    expect(outcome.status === 'analysed' && outcome.analysis.versionNumber).toBe(4);
  });

  it('reports an answer it cannot read as unavailable rather than showing part of one', async () => {
    const call = analyse();
    const broken = analysisPayload();
    (broken['allergens'] as Record<string, unknown>[])[0]['finding'] = 'Absent';
    http.expectOne(URL).flush(broken);

    expect((await call).status).toBe('unavailable');
  });

  it('carries the field errors from a refused selection', async () => {
    const call = analyse();
    http.expectOne(URL).flush(
      {
        code: 'nutrition.analysis.invalid',
        errors: { allergenIds: ['Choose no more than 20 allergens.'] },
      },
      { status: 400, statusText: 'Bad Request' },
    );

    const outcome = await call;
    expect(outcome.status).toBe('invalid_request');
    expect(outcome.status === 'invalid_request' && outcome.fieldErrors['allergenIds'][0]).toBe(
      'Choose no more than 20 allergens.',
    );
  });

  it('keeps a subject the catalogue will not accept apart from a malformed selection', async () => {
    const call = analyse();
    http.expectOne(URL).flush(
      {
        code: 'nutrition.analysis.subject.unprocessable',
        title: 'That allergen is no longer in the catalogue.',
      },
      { status: 422, statusText: 'Unprocessable Content' },
    );

    const outcome = await call;
    expect(outcome.status).toBe('subject_unprocessable');
    expect(outcome.status === 'subject_unprocessable' && outcome.message).toBe(
      'That allergen is no longer in the catalogue.',
    );
  });

  it('tells a missing version from an unreadable recipe by the stable code on the 404', async () => {
    const version = analyse();
    http.expectOne(URL).flush({ code: 'recipes.version.not_found' }, { status: 404, statusText: 'Not Found' });
    expect((await version).status).toBe('version_not_found');

    const recipe = analyse();
    http.expectOne(URL).flush({ code: 'recipes.recipe.not_found' }, { status: 404, statusText: 'Not Found' });
    expect((await recipe).status).toBe('not_found');
  });

  it('keeps a lost session apart from an outage, because the remedies differ', async () => {
    const unauthorized = analyse();
    http.expectOne(URL).flush({}, { status: 401, statusText: 'Unauthorized' });
    expect((await unauthorized).status).toBe('unauthorized');

    const forbidden = analyse();
    http.expectOne(URL).flush({}, { status: 403, statusText: 'Forbidden' });
    expect((await forbidden).status).toBe('unauthorized');

    const outage = analyse();
    http.expectOne(URL).flush({}, { status: 503, statusText: 'Service Unavailable' });
    expect((await outage).status).toBe('unavailable');
  });
});
