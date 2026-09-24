import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { RuntimeConfigService } from '../core/runtime-config.service';
import { absent, submitted } from '../models/recipe.models';
import { RecipeService } from './recipe.service';

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
  concurrencyToken: 'AAAAAAAAB9E=',
  currentVersion: null,
  ingredientGroups: [],
  instructionGroups: [],
  equipment: [],
  assetLinks: [],
  tags: [],
};

describe('RecipeService', () => {
  let service: RecipeService;
  let http: HttpTestingController;

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

  describe('createRecipe', () => {
    it('POSTs the encoded body with credentials and decodes a 201 response', async () => {
      const call = service.createRecipe('cozy-fall', { title: 'Chili', status: 'Ready' });
      const req = http.expectOne('https://gateway.example/api/v1/workspaces/cozy-fall/recipes');
      expect(req.request.method).toBe('POST');
      expect(req.request.withCredentials).toBeTrue();
      expect(req.request.body).toEqual({ title: 'Chili', status: 'Ready' });
      expect(req.request.headers.has('Idempotency-Key')).toBeFalse();

      req.flush(
        { recipeId: 'r1', title: 'Chili', status: 'Ready', versionId: 'v1', versionNumber: 1, createdAt: '2026-01-01T00:00:00Z' },
        { status: 201, statusText: 'Created' },
      );

      expect(await call).toEqual({
        status: 'created',
        recipe: { recipeId: 'r1', title: 'Chili', status: 'Ready', versionId: 'v1', versionNumber: 1, createdAt: '2026-01-01T00:00:00Z' },
        replayed: false,
      });
    });

    it('POSTs instructions and tags through to the request body unmodified', async () => {
      const instructions = [{ title: 'Batter', steps: [{ text: 'Mix.' }] }];
      const call = service.createRecipe('cozy-fall', { title: 'Chili', tags: ['quick', 'weeknight'], instructions });
      const req = http.expectOne('https://gateway.example/api/v1/workspaces/cozy-fall/recipes');
      expect(req.request.body).toEqual({ title: 'Chili', tags: ['quick', 'weeknight'], instructions });

      req.flush(
        { recipeId: 'r1', title: 'Chili', status: 'Draft', versionId: 'v1', versionNumber: 1, createdAt: '2026-01-01T00:00:00Z' },
        { status: 201, statusText: 'Created' },
      );
      await call;
    });

    it('reports a replayed idempotent create distinctly from a fresh one', async () => {
      const call = service.createRecipe('cozy-fall', { title: 'Chili' }, 'key-123');
      const req = http.expectOne('https://gateway.example/api/v1/workspaces/cozy-fall/recipes');
      req.flush(
        { recipeId: 'r1', title: 'Chili', status: 'Draft', versionId: 'v1', versionNumber: 1, createdAt: '2026-01-01T00:00:00Z' },
        { status: 201, statusText: 'Created', headers: { 'Idempotent-Replayed': 'true' } },
      );
      expect((await call) as { replayed: boolean }).toEqual(jasmine.objectContaining({ replayed: true }));
    });

    it('sends the Idempotency-Key header when one is supplied', async () => {
      const call = service.createRecipe('cozy-fall', { title: 'Chili' }, 'key-123');
      const req = http.expectOne('https://gateway.example/api/v1/workspaces/cozy-fall/recipes');
      expect(req.request.headers.get('Idempotency-Key')).toBe('key-123');
      req.flush(
        { recipeId: 'r1', title: 'Chili', status: 'Draft', versionId: 'v1', versionNumber: 1, createdAt: '2026-01-01T00:00:00Z' },
        { status: 201, statusText: 'Created' },
      );
      await call;
    });

    it('maps 400 to validation_failed', async () => {
      const call = service.createRecipe('cozy-fall', { title: '' });
      http.expectOne('https://gateway.example/api/v1/workspaces/cozy-fall/recipes').flush('bad', { status: 400, statusText: 'Bad Request' });
      expect(await call).toEqual({ status: 'validation_failed', fieldErrors: {} });
    });

    it('decodes per-field messages from a 400 ValidationProblemDetails body', async () => {
      const call = service.createRecipe('cozy-fall', { title: '' });
      http.expectOne('https://gateway.example/api/v1/workspaces/cozy-fall/recipes').flush(
        { title: 'Bad Request', errors: { title: ['Title is required.'] } },
        { status: 400, statusText: 'Bad Request' },
      );
      expect(await call).toEqual({ status: 'validation_failed', fieldErrors: { title: ['Title is required.'] } });
    });

    it('maps 422 to idempotency_key_conflict (reused key, different payload) rather than validation_failed', async () => {
      const call = service.createRecipe('cozy-fall', { title: 'x' }, 'reused-key');
      http.expectOne('https://gateway.example/api/v1/workspaces/cozy-fall/recipes').flush('dup', { status: 422, statusText: 'Unprocessable' });
      expect(await call).toEqual({ status: 'idempotency_key_conflict' });
    });

    it('maps 403 to forbidden and anything else to unavailable', async () => {
      const call = service.createRecipe('cozy-fall', { title: 'x' });
      http.expectOne('https://gateway.example/api/v1/workspaces/cozy-fall/recipes').flush('no', { status: 403, statusText: 'Forbidden' });
      expect(await call).toEqual({ status: 'forbidden' });

      const call2 = service.createRecipe('cozy-fall', { title: 'x' });
      http.expectOne('https://gateway.example/api/v1/workspaces/cozy-fall/recipes').flush('down', { status: 503, statusText: 'Unavailable' });
      expect(await call2).toEqual({ status: 'unavailable' });
    });
  });

  describe('getRecipeDetail', () => {
    it('GETs with credentials and decodes a 200 response', async () => {
      const call = service.getRecipeDetail('cozy-fall', 'r1');
      const req = http.expectOne('https://gateway.example/api/v1/workspaces/cozy-fall/recipes/r1');
      expect(req.request.method).toBe('GET');
      expect(req.request.withCredentials).toBeTrue();
      req.flush(RECIPE_DETAIL_WIRE);

      const result = await call;
      expect(result.status).toBe('found');
      expect(result.status === 'found' && result.recipe.id).toBe('r1');
      expect(result.status === 'found' && result.recipe.status).toBe('Draft');
    });

    it('maps 404 to not_found and anything else to unavailable', async () => {
      const call = service.getRecipeDetail('cozy-fall', 'missing');
      http.expectOne('https://gateway.example/api/v1/workspaces/cozy-fall/recipes/missing').flush('nope', { status: 404, statusText: 'Not Found' });
      expect(await call).toEqual({ status: 'not_found' });

      const call2 = service.getRecipeDetail('cozy-fall', 'r1');
      http.expectOne('https://gateway.example/api/v1/workspaces/cozy-fall/recipes/r1').flush('down', { status: 503, statusText: 'Unavailable' });
      expect(await call2).toEqual({ status: 'unavailable' });
    });
  });

  describe('updateRecipe', () => {
    it('PATCHes only submitted fields alongside the concurrency token, and decodes a 200 response', async () => {
      const call = service.updateRecipe('cozy-fall', 'r1', {
        expectedConcurrencyToken: 'AAAAAAAAB9E=',
        title: submitted('New Chili'),
        description: absent(),
      });
      const req = http.expectOne('https://gateway.example/api/v1/workspaces/cozy-fall/recipes/r1');
      expect(req.request.method).toBe('PATCH');
      expect(req.request.withCredentials).toBeTrue();
      expect(req.request.body).toEqual({ expectedConcurrencyToken: 'AAAAAAAAB9E=', title: 'New Chili' });
      expect('description' in req.request.body).toBeFalse();

      req.flush({ ...RECIPE_DETAIL_WIRE, title: 'New Chili', concurrencyToken: 'AAAAAAAAB9I=' });

      const result = await call;
      expect(result.status).toBe('updated');
      expect(result.status === 'updated' && result.recipe.title).toBe('New Chili');
      expect(result.status === 'updated' && result.replayed).toBeFalse();
    });

    it('PATCHes submitted instructions and tags through to the request body unmodified', async () => {
      const instructions = [{ id: 'g1', title: 'Batter', steps: [{ id: 's1', text: 'Mix.' }] }];
      const call = service.updateRecipe('cozy-fall', 'r1', {
        expectedConcurrencyToken: 'AAAAAAAAB9E=',
        tags: submitted(['quick']),
        instructions: submitted(instructions),
      });
      const req = http.expectOne('https://gateway.example/api/v1/workspaces/cozy-fall/recipes/r1');
      expect(req.request.body).toEqual({ expectedConcurrencyToken: 'AAAAAAAAB9E=', tags: ['quick'], instructions });
      req.flush(RECIPE_DETAIL_WIRE);
      await call;
    });

    it('reports a replayed idempotent update distinctly from a fresh one', async () => {
      const call = service.updateRecipe('cozy-fall', 'r1', { expectedConcurrencyToken: 'tok' }, 'key-456');
      const req = http.expectOne('https://gateway.example/api/v1/workspaces/cozy-fall/recipes/r1');
      req.flush(RECIPE_DETAIL_WIRE, { headers: { 'Idempotent-Replayed': 'true' } });
      const result = await call;
      expect(result.status === 'updated' && result.replayed).toBeTrue();
    });

    it('sends the Idempotency-Key header when one is supplied', async () => {
      const call = service.updateRecipe('cozy-fall', 'r1', { expectedConcurrencyToken: 'tok' }, 'key-456');
      const req = http.expectOne('https://gateway.example/api/v1/workspaces/cozy-fall/recipes/r1');
      expect(req.request.headers.get('Idempotency-Key')).toBe('key-456');
      req.flush(RECIPE_DETAIL_WIRE);
      await call;
    });

    it('maps 409 to conflict (stale concurrency token)', async () => {
      const call = service.updateRecipe('cozy-fall', 'r1', { expectedConcurrencyToken: 'stale' });
      http.expectOne('https://gateway.example/api/v1/workspaces/cozy-fall/recipes/r1').flush('conflict', { status: 409, statusText: 'Conflict' });
      expect(await call).toEqual({ status: 'conflict' });
    });

    it('maps 404 to not_found, 403 to forbidden, and 400 to validation_failed', async () => {
      const call = service.updateRecipe('cozy-fall', 'missing', { expectedConcurrencyToken: 'tok' });
      http.expectOne('https://gateway.example/api/v1/workspaces/cozy-fall/recipes/missing').flush('nope', { status: 404, statusText: 'Not Found' });
      expect(await call).toEqual({ status: 'not_found' });

      const call2 = service.updateRecipe('cozy-fall', 'r1', { expectedConcurrencyToken: 'tok' });
      http.expectOne('https://gateway.example/api/v1/workspaces/cozy-fall/recipes/r1').flush('no', { status: 403, statusText: 'Forbidden' });
      expect(await call2).toEqual({ status: 'forbidden' });

      const call3 = service.updateRecipe('cozy-fall', 'r1', { expectedConcurrencyToken: 'tok' });
      http.expectOne('https://gateway.example/api/v1/workspaces/cozy-fall/recipes/r1').flush('bad', { status: 400, statusText: 'Bad Request' });
      expect(await call3).toEqual({ status: 'validation_failed', fieldErrors: {} });
    });

    it('maps 422 to idempotency_key_conflict (reused key, different payload) rather than validation_failed', async () => {
      const call = service.updateRecipe('cozy-fall', 'r1', { expectedConcurrencyToken: 'tok' }, 'reused-key');
      http.expectOne('https://gateway.example/api/v1/workspaces/cozy-fall/recipes/r1').flush('dup', { status: 422, statusText: 'Unprocessable' });
      expect(await call).toEqual({ status: 'idempotency_key_conflict' });
    });
  });

  describe('setArchived', () => {
    const archiveUrl = 'https://gateway.example/api/v1/workspaces/cozy-fall/recipes/r1/archive';
    const unarchiveUrl = 'https://gateway.example/api/v1/workspaces/cozy-fall/recipes/r1/unarchive';
    const token = 'AAAAAAAAB9E=';

    it('POSTs the token to the archive route, and to the unarchive route coming back', async () => {
      const archiving = service.setArchived('cozy-fall', 'r1', true, { expectedConcurrencyToken: token });
      const archive = http.expectOne(archiveUrl);

      expect(archive.request.method).toBe('POST');
      expect(archive.request.withCredentials).toBeTrue();
      expect(archive.request.body).toEqual({ expectedConcurrencyToken: token });

      // Both commands are idempotent server-side and the routes take no key, so none is sent.
      expect(archive.request.headers.has('Idempotency-Key')).toBeFalse();

      archive.flush({ ...RECIPE_DETAIL_WIRE, status: 'Archived' });
      expect(await archiving).toEqual(jasmine.objectContaining({ status: 'updated' }));

      const bringingBack = service.setArchived('cozy-fall', 'r1', false, { expectedConcurrencyToken: token });
      const unarchive = http.expectOne(unarchiveUrl);
      expect(unarchive.request.body).toEqual({ expectedConcurrencyToken: token });

      unarchive.flush(RECIPE_DETAIL_WIRE);
      expect(await bringingBack).toEqual(jasmine.objectContaining({ status: 'updated' }));
    });

    /** Archiving an already-archived recipe succeeds and changes nothing, so a repeat is not a failure. */
    it('treats a repeated archive as the ordinary success it is', async () => {
      for (let attempt = 0; attempt < 2; attempt += 1) {
        const call = service.setArchived('cozy-fall', 'r1', true, { expectedConcurrencyToken: token });
        http.expectOne(archiveUrl).flush({ ...RECIPE_DETAIL_WIRE, status: 'Archived' });

        expect(await call).toEqual(jasmine.objectContaining({ status: 'updated' }));
      }
    });

    it('reports a stale token as a conflict, so nothing is shelved under a collaborator', async () => {
      const call = service.setArchived('cozy-fall', 'r1', true, { expectedConcurrencyToken: token });
      http.expectOne(archiveUrl).flush({ code: 'recipes.recipe.conflict' }, { status: 409, statusText: 'Conflict' });

      expect(await call).toEqual({ status: 'conflict' });
    });

    it('reports the Editor bar, an unreadable recipe and a refused body distinctly', async () => {
      const forbidden = service.setArchived('cozy-fall', 'r1', true, { expectedConcurrencyToken: token });
      http.expectOne(archiveUrl).flush({ code: 'recipes.recipe.forbidden' }, { status: 403, statusText: 'Forbidden' });
      expect(await forbidden).toEqual({ status: 'forbidden' });

      const missing = service.setArchived('cozy-fall', 'r1', true, { expectedConcurrencyToken: token });
      http.expectOne(archiveUrl).flush({ code: 'recipes.recipe.not_found' }, { status: 404, statusText: 'Not Found' });
      expect(await missing).toEqual({ status: 'not_found' });

      const invalid = service.setArchived('cozy-fall', 'r1', true, { expectedConcurrencyToken: 'not-a-token' });
      http
        .expectOne(archiveUrl)
        .flush(
          { code: 'recipes.recipe.invalid_request', errors: { expectedConcurrencyToken: ['That is not a token this API issued.'] } },
          { status: 400, statusText: 'Bad Request' },
        );
      expect(await invalid).toEqual({
        status: 'validation_failed',
        fieldErrors: { expectedConcurrencyToken: ['That is not a token this API issued.'] },
      });
    });

    it('treats a body it cannot decode as unavailable rather than as a moved recipe', async () => {
      const call = service.setArchived('cozy-fall', 'r1', true, { expectedConcurrencyToken: token });
      http.expectOne(archiveUrl).flush({ id: 'r1' });

      expect(await call).toEqual({ status: 'unavailable' });
    });
  });

  describe('duplicateRecipe', () => {
    const duplicateUrl = 'https://gateway.example/api/v1/workspaces/cozy-fall/recipes/r1/duplicate';
    const COPY_WIRE = {
      recipeId: 'r2',
      title: 'Cornbread, take two',
      status: 'Draft' as const,
      versionId: 'v1',
      versionNumber: 1,
      createdAt: '2026-03-12T14:02:00Z',
    };

    it('POSTs the title and version to the source recipe’s duplicate route, and decodes the copy', async () => {
      const call = service.duplicateRecipe('cozy-fall', 'r1', { title: '  Cornbread, take two  ', sourceVersionNumber: 3 });
      const req = http.expectOne(duplicateUrl);

      expect(req.request.method).toBe('POST');
      expect(req.request.withCredentials).toBeTrue();

      // A trimmed title, a version number, and nothing else: no token, because nothing is overwritten, and
      // no content, because everything but the title comes from the source.
      expect(req.request.body).toEqual({ title: 'Cornbread, take two', sourceVersionNumber: 3 });

      req.flush(COPY_WIRE, { status: 201, statusText: 'Created' });

      expect(await call).toEqual({ status: 'created', recipe: COPY_WIRE, replayed: false });
    });

    it('omits the version entirely when none was named, which the server reads as "as it currently stands"', async () => {
      const call = service.duplicateRecipe('cozy-fall', 'r1', { title: 'Cornbread, take two', sourceVersionNumber: null });
      const req = http.expectOne(duplicateUrl);

      expect(req.request.body).toEqual({ title: 'Cornbread, take two' });

      req.flush(COPY_WIRE, { status: 201, statusText: 'Created' });
      await call;
    });

    it('sends the idempotency key it was given and reports a replay', async () => {
      const call = service.duplicateRecipe('cozy-fall', 'r1', { title: 'Copy', sourceVersionNumber: 1 }, 'key-9');
      const req = http.expectOne(duplicateUrl);

      expect(req.request.headers.get('Idempotency-Key')).toBe('key-9');

      req.flush(COPY_WIRE, { status: 201, statusText: 'Created', headers: { 'Idempotent-Replayed': 'true' } });

      expect(await call).toEqual({ status: 'created', recipe: COPY_WIRE, replayed: true });
    });

    it('tells a missing source version apart from a recipe it may not see', async () => {
      const missingVersion = service.duplicateRecipe('cozy-fall', 'r1', { title: 'Copy', sourceVersionNumber: 9 });
      http
        .expectOne(duplicateUrl)
        .flush(
          { code: 'recipes.version.not_found', errors: { sourceVersionNumber: ['This recipe has no version 9.'] } },
          { status: 404, statusText: 'Not Found' },
        );

      expect(await missingVersion).toEqual({
        status: 'version_not_found',
        fieldErrors: { sourceVersionNumber: ['This recipe has no version 9.'] },
      });

      const missingRecipe = service.duplicateRecipe('cozy-fall', 'r1', { title: 'Copy', sourceVersionNumber: 1 });
      http.expectOne(duplicateUrl).flush({ code: 'recipes.recipe.not_found' }, { status: 404, statusText: 'Not Found' });

      expect(await missingRecipe).toEqual({ status: 'not_found' });
    });

    it('reports a refused title with its field errors, and the Contributor bar as forbidden', async () => {
      const invalid = service.duplicateRecipe('cozy-fall', 'r1', { title: '', sourceVersionNumber: 1 });
      http
        .expectOne(duplicateUrl)
        .flush(
          { code: 'recipes.recipe.invalid_request', errors: { title: ['Give the copy a title.'] } },
          { status: 400, statusText: 'Bad Request' },
        );
      expect(await invalid).toEqual({ status: 'validation_failed', fieldErrors: { title: ['Give the copy a title.'] } });

      const forbidden = service.duplicateRecipe('cozy-fall', 'r1', { title: 'Copy', sourceVersionNumber: 1 });
      http.expectOne(duplicateUrl).flush({ code: 'recipes.recipe.forbidden' }, { status: 403, statusText: 'Forbidden' });
      expect(await forbidden).toEqual({ status: 'forbidden' });

      const reused = service.duplicateRecipe('cozy-fall', 'r1', { title: 'Copy', sourceVersionNumber: 1 });
      http.expectOne(duplicateUrl).flush({ code: 'idempotency.key.conflict' }, { status: 422, statusText: 'Unprocessable' });
      expect(await reused).toEqual({ status: 'idempotency_key_conflict' });
    });

    it('treats a body it cannot decode as unavailable rather than as a copy that exists', async () => {
      const call = service.duplicateRecipe('cozy-fall', 'r1', { title: 'Copy', sourceVersionNumber: 1 });
      http.expectOne(duplicateUrl).flush({ recipeId: 'r2' }, { status: 201, statusText: 'Created' });

      expect(await call).toEqual({ status: 'unavailable' });
    });
  });
});
