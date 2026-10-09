import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { RuntimeConfigService } from '../core/runtime-config.service';
import { DamAssetService } from './dam-asset.service';

const GATEWAY = 'https://gateway.example';
const ROUTE = `${GATEWAY}/api/v1/workspaces/cozy-fall/dam-assets/from-generated-image`;
const IMAGE = '0f4b2c9a-1111-4222-8333-444455556666';
const ASSET = '7a1d3e5f-2222-4333-8444-555566667777';

const BODY = { generatedImageId: IMAGE, metadata: { title: 'Soda bread hero' } };

function assetBody(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    id: ASSET,
    title: 'Soda bread hero',
    kind: 'AiGenerated',
    currentVersionNumber: 1,
    mediaType: 'image/png',
    width: 1024,
    height: 1024,
    sizeBytes: 204800,
    sourceGeneratedImageId: IMAGE,
    promptRecordId: null,
    recipeId: null,
    createdAt: '2026-10-09T12:00:00+00:00',
    ...overrides,
  };
}

function problem(code: string, extra: Record<string, unknown> = {}): Record<string, unknown> {
  return { code, title: 'That could not be done.', traceId: 'trace-1', ...extra };
}

describe('DamAssetService — keeping a generated picture', () => {
  let service: DamAssetService;
  let http: HttpTestingController;

  beforeEach(async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);

    const runtimeConfig = TestBed.inject(RuntimeConfigService);
    const loading = runtimeConfig.load();
    http.expectOne('/runtime-config.json').flush({ gatewayUrl: GATEWAY });
    await loading;

    service = TestBed.inject(DamAssetService);
  });

  afterEach(() => http.verify());

  it('posts the body to the workspace route with credentials and the idempotency key', async () => {
    const pending = service.keepGeneratedImage('cozy-fall', BODY, 'key-1');

    const request = http.expectOne(ROUTE);
    expect(request.request.method).toBe('POST');
    expect(request.request.withCredentials).toBeTrue();
    expect(request.request.headers.get('Idempotency-Key')).toBe('key-1');
    expect(request.request.body).toEqual(BODY);
    request.flush(assetBody({ promptRecordId: 'p1', recipeId: 'r1' }), { status: 201, statusText: 'Created' });

    const outcome = await pending;
    expect(outcome.status).toBe('saved');
    if (outcome.status === 'saved') {
      expect(outcome.asset.id).toBe(ASSET);
      expect(outcome.asset.kind).toBe('AiGenerated');
      expect(outcome.asset.sourceGeneratedImageId).toBe(IMAGE);
      expect(outcome.asset.promptRecordId).toBe('p1');
      expect(outcome.asset.recipeId).toBe('r1');
    }
  });

  it('encodes the slug into the path and never puts a workspace in the body', async () => {
    const pending = service.keepGeneratedImage('a b/c', BODY, 'key-1');

    const request = http.expectOne(`${GATEWAY}/api/v1/workspaces/a%20b%2Fc/dam-assets/from-generated-image`);
    expect(JSON.stringify(request.request.body)).not.toContain('workspace');
    request.flush(assetBody(), { status: 201, statusText: 'Created' });

    expect((await pending).status).toBe('saved');
  });

  it('answers unavailable for a reply it cannot read as an asset', async () => {
    const pending = service.keepGeneratedImage('cozy-fall', BODY, 'key-1');
    http.expectOne(ROUTE).flush({ id: ASSET }, { status: 201, statusText: 'Created' });

    expect(await pending).toEqual({ status: 'unavailable' });
  });

  it("carries the server's field errors for a refused entry", async () => {
    const pending = service.keepGeneratedImage('cozy-fall', BODY, 'key-1');
    http.expectOne(ROUTE).flush(
      problem('media.asset.invalid_request', {
        title: 'The asset could not be created.',
        errors: { workspaceTagIds: ['One of those tags is not in this workspace.'] },
      }),
      { status: 400, statusText: 'Bad Request' },
    );

    expect(await pending).toEqual({
      status: 'invalid',
      message: 'The asset could not be created.',
      fieldErrors: { workspaceTagIds: ['One of those tags is not in this workspace.'] },
    });
  });

  it("reports the prompt's own refusal as the prompt's, whatever status it arrives as", async () => {
    for (const [code, status] of [
      ['content.prompt.invalid', 400],
      ['content.prompt.lineage.unprocessable', 422],
      ['content.prompt.not_found', 404],
      ['content.prompt.conflict', 409],
    ] as const) {
      const pending = service.keepGeneratedImage('cozy-fall', BODY, 'key-1');
      http.expectOne(ROUTE).flush(problem(code, { title: 'The prompt could not be saved.' }), { status, statusText: 'Refused' });

      expect(await pending).withContext(code).toEqual({ status: 'prompt_refused', message: 'The prompt could not be saved.' });
    }
  });

  it('tells a spent key apart from a refused entry, because a new key is the remedy', async () => {
    const pending = service.keepGeneratedImage('cozy-fall', BODY, 'key-1');
    http.expectOne(ROUTE).flush(problem('idempotency.key_reused'), { status: 422, statusText: 'Unprocessable' });

    expect(await pending).toEqual({ status: 'key_reused' });
  });

  it('maps a refusal for lack of access, a picture that is not there, and failures worth retrying', async () => {
    for (const [code, status, expected] of [
      ['media.asset.forbidden', 403, 'forbidden'],
      ['media.asset.not_found', 404, 'image_gone'],
      ['media.asset.conflict', 409, 'unavailable'],
      ['media.asset.unavailable', 503, 'unavailable'],
    ] as const) {
      const pending = service.keepGeneratedImage('cozy-fall', BODY, 'key-1');
      http.expectOne(ROUTE).flush(problem(code), { status, statusText: 'Refused' });

      expect((await pending).status).withContext(code).toBe(expected);
    }
  });
});
