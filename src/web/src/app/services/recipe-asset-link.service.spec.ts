import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { RuntimeConfigService } from '../core/runtime-config.service';
import { mediaRecipe } from '../features/recipes/recipe-media.testing';
import { LinkRecipeAssetRequest } from '../models/recipe-asset-link.models';
import { RecipeAssetLinkService } from './recipe-asset-link.service';

const GATEWAY = 'https://gateway.example';
const BASE = `${GATEWAY}/api/v1/workspaces/cozy-fall/recipes/r1/asset-links`;

const REQUEST: LinkRecipeAssetRequest = {
  mediaAssetId: 'a1',
  role: 'Step',
  instructionStepId: 's2',
  versionNumber: 2,
  caption: 'Folding in.',
  expectedConcurrencyToken: 'token-1',
};

function problem(code: string, errors?: Record<string, string[]>): Record<string, unknown> {
  return { code, title: 'That could not be done.', traceId: 'trace-1', ...(errors ? { errors } : {}) };
}

describe('RecipeAssetLinkService', () => {
  let service: RecipeAssetLinkService;
  let http: HttpTestingController;

  beforeEach(async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);

    const loading = TestBed.inject(RuntimeConfigService).load();
    http.expectOne('/runtime-config.json').flush({ gatewayUrl: GATEWAY });
    await loading;

    service = TestBed.inject(RecipeAssetLinkService);
  });

  afterEach(() => http.verify());

  describe('link', () => {
    it('posts the link with the session cookie and the idempotency key, and answers with the recipe', async () => {
      const pending = service.link('cozy-fall', 'r1', REQUEST, 'key-1');

      const sent = http.expectOne(BASE);
      expect(sent.request.method).toBe('POST');
      expect(sent.request.withCredentials).toBeTrue();
      expect(sent.request.headers.get('Idempotency-Key')).toBe('key-1');
      expect(sent.request.body).toEqual({
        mediaAssetId: 'a1',
        role: 'Step',
        instructionStepId: 's2',
        versionNumber: 2,
        caption: 'Folding in.',
        expectedConcurrencyToken: 'token-1',
      });

      sent.flush(mediaRecipe());

      const outcome = await pending;
      expect(outcome.status).toBe('linked');
      if (outcome.status === 'linked') {
        expect(outcome.recipe.concurrencyToken).toBe('AAAAAAAAB9I=');
        expect(outcome.replayed).toBeFalse();
      }
    });

    it('reports a replay as the same success', async () => {
      const pending = service.link('cozy-fall', 'r1', REQUEST, 'key-1');
      http.expectOne(BASE).flush(mediaRecipe(), { headers: { 'Idempotent-Replayed': 'true' } });

      const outcome = await pending;
      expect(outcome.status === 'linked' && outcome.replayed).toBeTrue();
    });

    it('encodes the workspace and the recipe into the path', async () => {
      const pending = service.link('a/b', 'r 1', REQUEST, 'key-1');
      http.expectOne(`${GATEWAY}/api/v1/workspaces/a%2Fb/recipes/r%201/asset-links`).flush(mediaRecipe());

      expect((await pending).status).toBe('linked');
    });

    it('treats a body that is not a recipe as no answer', async () => {
      const pending = service.link('cozy-fall', 'r1', REQUEST, 'key-1');
      http.expectOne(BASE).flush({ nope: true });

      expect((await pending).status).toBe('unavailable');
    });

    it('carries field errors on a malformed request', async () => {
      const pending = service.link('cozy-fall', 'r1', REQUEST, 'key-1');
      http
        .expectOne(BASE)
        .flush(problem('recipes.assetLink.invalid_request', { caption: ['Too long.'] }), { status: 400, statusText: 'Bad Request' });

      expect(await pending).toEqual({ status: 'validation_failed', fieldErrors: { caption: ['Too long.'] } });
    });

    it('tells a target that is not there from a spent idempotency key, though both are 422', async () => {
      const refused = service.link('cozy-fall', 'r1', REQUEST, 'key-1');
      http
        .expectOne(BASE)
        .flush(problem('recipes.assetLink.target.unprocessable', { mediaAssetId: ["That picture is not in this workspace's library."] }), {
          status: 422,
          statusText: 'Unprocessable Entity',
        });

      expect(await refused).toEqual({
        status: 'target_refused',
        fieldErrors: { mediaAssetId: ["That picture is not in this workspace's library."] },
      });

      const reused = service.link('cozy-fall', 'r1', REQUEST, 'key-1');
      http.expectOne(BASE).flush(problem('idempotency.key_reused'), { status: 422, statusText: 'Unprocessable Entity' });

      expect((await reused).status).toBe('idempotency_key_conflict');
    });

    const conflicts: readonly (readonly [string, string])[] = [
      ['recipes.assetLink.hero.conflict', 'hero_exists'],
      ['recipes.assetLink.duplicate.conflict', 'already_linked'],
      ['recipes.archived.conflict', 'archived_conflict'],
      ['recipes.recipe.conflict', 'conflict'],
    ];

    for (const [code, status] of conflicts) {
      it(`maps 409 ${code} to ${status}`, async () => {
        const pending = service.link('cozy-fall', 'r1', REQUEST, 'key-1');
        http.expectOne(BASE).flush(problem(code), { status: 409, statusText: 'Conflict' });

        expect((await pending).status).toBe(status);
      });
    }

    it('maps 403, 404 and anything else', async () => {
      for (const [status, expected] of [
        [403, 'forbidden'],
        [404, 'not_found'],
        [503, 'unavailable'],
      ] as const) {
        const pending = service.link('cozy-fall', 'r1', REQUEST, 'key-1');
        http.expectOne(BASE).flush(problem('x'), { status, statusText: 'x' });

        expect((await pending).status).withContext(String(status)).toBe(expected);
      }
    });
  });

  describe('unlink', () => {
    it('deletes the link, quoting the token in the body, and answers with the recipe', async () => {
      const pending = service.unlink('cozy-fall', 'r1', 'l1', 'token-1', 'key-2');

      const sent = http.expectOne(`${BASE}/l1`);
      expect(sent.request.method).toBe('DELETE');
      expect(sent.request.withCredentials).toBeTrue();
      expect(sent.request.headers.get('Idempotency-Key')).toBe('key-2');
      expect(sent.request.body).toEqual({ expectedConcurrencyToken: 'token-1' });

      sent.flush(mediaRecipe({ assetLinks: [] }));

      const outcome = await pending;
      expect(outcome.status).toBe('unlinked');
      if (outcome.status === 'unlinked') expect(outcome.recipe.assetLinks).toEqual([]);
    });

    it('tells a link that is already gone from a recipe that cannot be seen, though both are 404', async () => {
      const gone = service.unlink('cozy-fall', 'r1', 'l1', 'token-1', 'key-2');
      http.expectOne(`${BASE}/l1`).flush(problem('recipes.assetLink.not_found'), { status: 404, statusText: 'Not Found' });
      expect((await gone).status).toBe('link_not_found');

      const hidden = service.unlink('cozy-fall', 'r1', 'l1', 'token-1', 'key-2');
      http.expectOne(`${BASE}/l1`).flush(problem('recipes.recipe.not_found'), { status: 404, statusText: 'Not Found' });
      expect((await hidden).status).toBe('not_found');
    });

    it('maps the conflicts, a spent key, a refusal and no answer', async () => {
      for (const [status, code, expected] of [
        [409, 'recipes.recipe.conflict', 'conflict'],
        [409, 'recipes.archived.conflict', 'archived_conflict'],
        [422, 'idempotency.key_reused', 'idempotency_key_conflict'],
        [403, 'x', 'forbidden'],
        [500, 'x', 'unavailable'],
      ] as const) {
        const pending = service.unlink('cozy-fall', 'r1', 'l1', 'token-1', 'key-2');
        http.expectOne(`${BASE}/l1`).flush(problem(code), { status, statusText: 'x' });

        expect((await pending).status).withContext(`${status} ${code}`).toBe(expected);
      }
    });
  });
});
