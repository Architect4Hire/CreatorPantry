import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { RuntimeConfigService } from '../core/runtime-config.service';
import { RecipeService } from './recipe.service';

const HISTORY_ENTRY_WIRE = {
  id: 'v2',
  versionNumber: 2,
  source: 'CreatorEdit',
  readiness: 'Draft',
  reason: 'Reworded the headnote',
  createdAt: '2026-01-02T00:00:00Z',
  createdByName: 'Sam Okafor',
  parentVersionId: 'v1',
  restoredFromVersionId: null,
  aiProposalId: null,
};

const SIDE_WIRE = {
  versionId: 'v1',
  versionNumber: 1,
  source: 'CreatorEdit',
  readiness: 'Draft',
  createdAt: '2026-01-01T00:00:00Z',
};

function comparisonWire(sections: unknown[], hasChanges = true): Record<string, unknown> {
  return {
    from: SIDE_WIRE,
    to: { ...SIDE_WIRE, versionId: 'v2', versionNumber: 2, createdAt: '2026-01-02T00:00:00Z' },
    comparison: { sections, hasChanges },
  };
}

function sectionWire(section: string, fieldChanges: unknown[] = [], itemChanges: unknown[] = []): Record<string, unknown> {
  return {
    section,
    fieldChanges,
    itemChanges,
    hasChanges: fieldChanges.length > 0 || itemChanges.length > 0,
  };
}

/**
 * A recipe detail as the wire carries it, which is what a restore answers with: the whole recipe, in the
 * same shape a read returns, carrying the refreshed token the next write must quote.
 */
const RECIPE_DETAIL_WIRE = {
  id: 'r1',
  title: 'Chili',
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
  status: 'Draft',
  duplicatedFrom: null,
  createdAt: '2026-01-01T00:00:00Z',
  updatedAt: '2026-01-02T00:00:00Z',
  concurrencyToken: 'AAAAAAAAB9I=',
  currentVersion: null,
  ingredientGroups: [],
  instructionGroups: [],
  equipment: [],
  assetLinks: [],
  tags: [],
};

describe('RecipeService version routes', () => {
  let service: RecipeService;
  let http: HttpTestingController;

  const historyUrl = 'https://gateway.example/api/v1/workspaces/cozy-fall/recipes/r1/versions';
  const compareUrl = `${historyUrl}/compare`;
  const restoreUrl = `${historyUrl}/3/restore`;

  beforeEach(async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);

    const runtimeConfig = TestBed.inject(RuntimeConfigService);
    const loading = runtimeConfig.load();
    http.expectOne('/runtime-config.json').flush({ gatewayUrl: 'https://gateway.example' });
    await loading;

    service = TestBed.inject(RecipeService);
  });

  afterEach(() => http.verify());

  describe('getVersionHistory', () => {
    it('GETs the versions route with credentials and decodes a page', async () => {
      const call = service.getVersionHistory('cozy-fall', 'r1');
      const req = http.expectOne(historyUrl);

      expect(req.request.method).toBe('GET');
      expect(req.request.withCredentials).toBeTrue();
      expect(req.request.params.has('cursor')).toBeFalse();

      req.flush({ items: [HISTORY_ENTRY_WIRE], nextCursor: 'next' });

      const outcome = await call;
      expect(outcome.status).toBe('found');
      expect(outcome).toEqual(
        jasmine.objectContaining({ page: { items: [HISTORY_ENTRY_WIRE], nextCursor: 'next' } }),
      );
    });

    it('sends a cursor when one is supplied', async () => {
      const call = service.getVersionHistory('cozy-fall', 'r1', 'abc');
      const req = http.expectOne((request) => request.url === historyUrl);

      expect(req.request.params.get('cursor')).toBe('abc');

      req.flush({ items: [], nextCursor: null });
      await call;
    });

    it('reports a stale cursor distinctly, because its remedy is to start the list again', async () => {
      const call = service.getVersionHistory('cozy-fall', 'r1', 'stale');
      http
        .expectOne((request) => request.url === historyUrl)
        .flush({ code: 'recipes.cursor.invalid_request' }, { status: 400, statusText: 'Bad Request' });

      expect(await call).toEqual({ status: 'cursor_expired' });
    });

    it('reports a 404 as not_found', async () => {
      const call = service.getVersionHistory('cozy-fall', 'r1');
      http.expectOne(historyUrl).flush({ code: 'recipes.recipe.not_found' }, { status: 404, statusText: 'Not Found' });

      expect(await call).toEqual({ status: 'not_found' });
    });

    it('treats a body it cannot decode as unavailable rather than as an empty history', async () => {
      const call = service.getVersionHistory('cozy-fall', 'r1');
      http.expectOne(historyUrl).flush({ items: [{ id: 'v1' }], nextCursor: null });

      expect(await call).toEqual({ status: 'unavailable' });
    });
  });

  describe('compareVersions', () => {
    it('GETs the compare route with both version numbers and decodes the answer', async () => {
      const call = service.compareVersions('cozy-fall', 'r1', 1, 2);
      const req = http.expectOne((request) => request.url === compareUrl);

      expect(req.request.method).toBe('GET');
      expect(req.request.withCredentials).toBeTrue();
      expect(req.request.params.get('from')).toBe('1');
      expect(req.request.params.get('to')).toBe('2');

      req.flush(
        comparisonWire([sectionWire('Metadata', [{ field: 'Title', from: 'Chili', to: 'Chilli' }])]),
      );

      const outcome = await call;
      expect(outcome.status).toBe('found');

      if (outcome.status !== 'found') return;
      expect(outcome.comparison.from.versionNumber).toBe(1);
      expect(outcome.comparison.comparison.sections[0].fieldChanges[0].field).toBe('Title');
    });

    it('sends the numbers in the order asked for, so the direction is the caller’s', async () => {
      const call = service.compareVersions('cozy-fall', 'r1', 5, 2);
      const req = http.expectOne((request) => request.url === compareUrl);

      expect(req.request.params.get('from')).toBe('5');
      expect(req.request.params.get('to')).toBe('2');

      req.flush(comparisonWire([], false));
      await call;
    });

    it('tells a missing version apart from an unreadable recipe by the stable code', async () => {
      const missingVersion = service.compareVersions('cozy-fall', 'r1', 1, 9);
      http
        .expectOne((request) => request.url === compareUrl)
        .flush(
          { code: 'recipes.version.not_found', errors: { to: ['This recipe has no version 9.'] } },
          { status: 404, statusText: 'Not Found' },
        );

      expect(await missingVersion).toEqual({
        status: 'version_not_found',
        fieldErrors: { to: ['This recipe has no version 9.'] },
      });

      const missingRecipe = service.compareVersions('cozy-fall', 'r1', 1, 2);
      http
        .expectOne((request) => request.url === compareUrl)
        .flush({ code: 'recipes.recipe.not_found' }, { status: 404, statusText: 'Not Found' });

      expect(await missingRecipe).toEqual({ status: 'not_found' });
    });

    it('reports a refused query with its field errors', async () => {
      const call = service.compareVersions('cozy-fall', 'r1', 0, 2);
      http
        .expectOne((request) => request.url === compareUrl)
        .flush(
          { code: 'recipes.comparison.invalid_request', errors: { from: ['Version numbers start at 1.'] } },
          { status: 400, statusText: 'Bad Request' },
        );

      expect(await call).toEqual({
        status: 'invalid_request',
        fieldErrors: { from: ['Version numbers start at 1.'] },
      });
    });

    it('treats a body it cannot decode as unavailable rather than as a comparison with no changes', async () => {
      const call = service.compareVersions('cozy-fall', 'r1', 1, 2);
      http.expectOne((request) => request.url === compareUrl).flush({ from: SIDE_WIRE, to: SIDE_WIRE });

      // A half-decoded comparison would understate what changed, which is the one mistake a diff must not make.
      expect(await call).toEqual({ status: 'unavailable' });
    });
  });

  describe('restoreVersion', () => {
    const token = 'AAAAAAAAB9E=';

    it('POSTs to the version’s own restore route, carrying the token and nothing about the recipe', async () => {
      const call = service.restoreVersion('cozy-fall', 'r1', 3, { expectedConcurrencyToken: token, reason: 'Read better.' });
      const req = http.expectOne(restoreUrl);

      expect(req.request.method).toBe('POST');
      expect(req.request.withCredentials).toBeTrue();

      // Which version to restore is the route's segment, and no content is submitted: everything restored
      // comes from the archive, which is what stops an edit being smuggled into a restore.
      expect(req.request.body).toEqual({ expectedConcurrencyToken: token, reason: 'Read better.' });

      req.flush({ ...RECIPE_DETAIL_WIRE, title: 'Chilli' });

      const outcome = await call;
      expect(outcome.status).toBe('restored');
      expect(outcome).toEqual(jasmine.objectContaining({ replayed: false }));
    });

    it('trims the reason, and omits it entirely when there is nothing to record', async () => {
      const trimmed = service.restoreVersion('cozy-fall', 'r1', 3, { expectedConcurrencyToken: token, reason: '  Read better.  ' });
      const first = http.expectOne(restoreUrl);
      expect(first.request.body).toEqual({ expectedConcurrencyToken: token, reason: 'Read better.' });
      first.flush(RECIPE_DETAIL_WIRE);
      await trimmed;

      const blank = service.restoreVersion('cozy-fall', 'r1', 3, { expectedConcurrencyToken: token, reason: '   ' });
      const second = http.expectOne(restoreUrl);
      expect(second.request.body).toEqual({ expectedConcurrencyToken: token });
      second.flush(RECIPE_DETAIL_WIRE);
      await blank;
    });

    it('sends the idempotency key it was given, and none when it was given none', async () => {
      const keyed = service.restoreVersion('cozy-fall', 'r1', 3, { expectedConcurrencyToken: token, reason: null }, 'key-1');
      const first = http.expectOne(restoreUrl);
      expect(first.request.headers.get('Idempotency-Key')).toBe('key-1');
      first.flush(RECIPE_DETAIL_WIRE, { headers: { 'Idempotent-Replayed': 'true' } });
      expect(await keyed).toEqual(jasmine.objectContaining({ status: 'restored', replayed: true }));

      const unkeyed = service.restoreVersion('cozy-fall', 'r1', 3, { expectedConcurrencyToken: token, reason: null });
      const second = http.expectOne(restoreUrl);
      expect(second.request.headers.has('Idempotency-Key')).toBeFalse();
      second.flush(RECIPE_DETAIL_WIRE);
      await unkeyed;
    });

    it('reports a stale token as a recoverable conflict', async () => {
      const call = service.restoreVersion('cozy-fall', 'r1', 3, { expectedConcurrencyToken: token, reason: null });
      http.expectOne(restoreUrl).flush({ code: 'recipes.recipe.conflict' }, { status: 409, statusText: 'Conflict' });

      expect(await call).toEqual({ status: 'conflict' });
    });

    /** Both are 409s and the remedies are opposites, so the stable code is what has to tell them apart. */
    it('reports an archived recipe as its own conflict, not as a stale token', async () => {
      const call = service.restoreVersion('cozy-fall', 'r1', 3, { expectedConcurrencyToken: token, reason: null });
      http.expectOne(restoreUrl).flush({ code: 'recipes.archived.conflict' }, { status: 409, statusText: 'Conflict' });

      expect(await call).toEqual({ status: 'archived_conflict' });
    });

    it('tells a missing version apart from a recipe it may not see', async () => {
      const missingVersion = service.restoreVersion('cozy-fall', 'r1', 3, { expectedConcurrencyToken: token, reason: null });
      http
        .expectOne(restoreUrl)
        .flush(
          { code: 'recipes.version.not_found', errors: { versionNumber: ['This recipe has no version 3.'] } },
          { status: 404, statusText: 'Not Found' },
        );

      expect(await missingVersion).toEqual({
        status: 'version_not_found',
        fieldErrors: { versionNumber: ['This recipe has no version 3.'] },
      });

      const missingRecipe = service.restoreVersion('cozy-fall', 'r1', 3, { expectedConcurrencyToken: token, reason: null });
      http.expectOne(restoreUrl).flush({ code: 'recipes.recipe.not_found' }, { status: 404, statusText: 'Not Found' });

      expect(await missingRecipe).toEqual({ status: 'not_found' });
    });

    it('reports a refused body with its field errors', async () => {
      const call = service.restoreVersion('cozy-fall', 'r1', 3, { expectedConcurrencyToken: token, reason: 'x'.repeat(1001) });
      http
        .expectOne(restoreUrl)
        .flush(
          { code: 'recipes.recipe.invalid_request', errors: { reason: ['That reason is too long.'] } },
          { status: 400, statusText: 'Bad Request' },
        );

      expect(await call).toEqual({ status: 'validation_failed', fieldErrors: { reason: ['That reason is too long.'] } });
    });

    it('reports the Editor bar as forbidden and a reused key as its own failure', async () => {
      const forbidden = service.restoreVersion('cozy-fall', 'r1', 3, { expectedConcurrencyToken: token, reason: null });
      http.expectOne(restoreUrl).flush({ code: 'recipes.recipe.forbidden' }, { status: 403, statusText: 'Forbidden' });
      expect(await forbidden).toEqual({ status: 'forbidden' });

      const reused = service.restoreVersion('cozy-fall', 'r1', 3, { expectedConcurrencyToken: token, reason: null });
      http.expectOne(restoreUrl).flush({ code: 'idempotency.key.conflict' }, { status: 422, statusText: 'Unprocessable' });
      expect(await reused).toEqual({ status: 'idempotency_key_conflict' });
    });

    it('treats a body it cannot decode as unavailable rather than as a restored recipe', async () => {
      const call = service.restoreVersion('cozy-fall', 'r1', 3, { expectedConcurrencyToken: token, reason: null });
      http.expectOne(restoreUrl).flush({ id: 'r1' });

      expect(await call).toEqual({ status: 'unavailable' });
    });
  });
});
