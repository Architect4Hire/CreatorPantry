import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';

import { RuntimeConfigService } from '../core/runtime-config.service';
import { GeneratedImageService } from './generated-image.service';

const GATEWAY = 'https://gateway.example';
const BASE = `${GATEWAY}/api/v1/workspaces/cozy-fall/generated-images`;
const IMAGE_ID = '0f4b2c9a-1111-4222-8333-444455556666';
const OPERATION_ID = 'aa11bb22-3333-4444-8555-666677778888';

function operationBody(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    id: OPERATION_ID,
    status: 'Requested',
    variantCount: 2,
    stagedCount: 0,
    providerName: null,
    modelName: null,
    failureCategory: null,
    failureSummary: null,
    requestedAt: '2026-10-08T12:00:00Z',
    completedAt: null,
    ...overrides,
  };
}

function stagedRow(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    id: IMAGE_ID,
    variantIndex: 1,
    status: 'Staged',
    mediaType: 'image/png',
    width: 1024,
    height: 1024,
    sizeBytes: 482000,
    retentionExpiresAt: '2026-10-09T12:00:00Z',
    createdAt: '2026-10-08T12:00:00Z',
    ...overrides,
  };
}

function problem(code: string, extra: Record<string, unknown> = {}): Record<string, unknown> {
  return { code, title: 'That could not be done.', traceId: 'trace-1', ...extra };
}

/**
 * The same ProblemDetails, as the bytes a `responseType: 'blob'` request gets it as.
 *
 * The preview asks for a Blob, so its refusals arrive as one too — and the test double will not convert a
 * JSON object into bytes on its own.
 */
function problemBlob(code: string): Blob {
  return new Blob([JSON.stringify(problem(code))], { type: 'application/problem+json' });
}

describe('GeneratedImageService', () => {
  let service: GeneratedImageService;
  let http: HttpTestingController;

  beforeEach(async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);

    const runtimeConfig = TestBed.inject(RuntimeConfigService);
    const loading = runtimeConfig.load();
    http.expectOne('/runtime-config.json').flush({ gatewayUrl: GATEWAY });
    await loading;

    service = TestBed.inject(GeneratedImageService);
  });

  afterEach(() => http.verify());

  describe('request', () => {
    it('posts the body the route takes, with the required idempotency key as a header', async () => {
      const pending = service.request(
        'cozy-fall',
        { promptText: 'A tight crop, soft light.', avoidText: 'clutter', variantCount: 2 },
        'key-1',
      );

      const request = http.expectOne(BASE);
      expect(request.request.method).toBe('POST');
      expect(request.request.headers.get('Idempotency-Key')).toBe('key-1');
      expect(request.request.withCredentials).toBeTrue();
      expect(request.request.body).toEqual({
        promptText: 'A tight crop, soft light.',
        avoidText: 'clutter',
        aiProposalId: null,
        variantCount: 2,
      });

      request.flush(operationBody(), { status: 202, statusText: 'Accepted' });

      const outcome = await pending;
      expect(outcome.status).toBe('accepted');
      expect(outcome.status === 'accepted' && outcome.operation.id).toBe(OPERATION_ID);
    });

    it('reads a replay as the accepted answer it is, because the pictures are already coming', async () => {
      const pending = service.request('cozy-fall', { promptText: 'A crop.', avoidText: null, variantCount: 1 }, 'key-1');

      http
        .expectOne(BASE)
        .flush(operationBody({ status: 'Running', stagedCount: 1 }), { status: 202, statusText: 'Accepted' });

      expect((await pending).status).toBe('accepted');
    });

    it('carries the server’s own words and field errors for a refused request', async () => {
      const pending = service.request('cozy-fall', { promptText: '', avoidText: null, variantCount: 9 }, 'key-1');

      http.expectOne(BASE).flush(
        problem('media.generation.invalid_request', {
          title: 'The images could not be requested.',
          errors: { VariantCount: ['Ask for between 1 and 4 images.'] },
        }),
        { status: 400, statusText: 'Bad Request' },
      );

      const outcome = await pending;
      expect(outcome.status).toBe('refused');
      expect(outcome.status === 'refused' && outcome.message).toBe('The images could not be requested.');
      expect(outcome.status === 'refused' && outcome.fieldErrors['VariantCount']).toEqual([
        'Ask for between 1 and 4 images.',
      ]);
    });

    it('tells a missing idempotency key apart from a refusal, because nothing a creator did caused it', async () => {
      const pending = service.request('cozy-fall', { promptText: 'A crop.', avoidText: null, variantCount: 1 }, '');

      http
        .expectOne(BASE)
        .flush(problem('idempotency.key_required'), { status: 400, statusText: 'Bad Request' });

      expect((await pending).status).toBe('key_required');
    });

    it('reads a Viewer’s refusal as forbidden', async () => {
      const pending = service.request('cozy-fall', { promptText: 'A crop.', avoidText: null, variantCount: 1 }, 'key-1');

      http
        .expectOne(BASE)
        .flush(problem('media.generation.forbidden'), { status: 403, statusText: 'Forbidden' });

      expect((await pending).status).toBe('forbidden');
    });

    it('reads the edge’s rate limit as its own outcome, because waiting is the remedy', async () => {
      const pending = service.request('cozy-fall', { promptText: 'A crop.', avoidText: null, variantCount: 1 }, 'key-1');

      http.expectOne(BASE).flush(null, { status: 429, statusText: 'Too Many Requests' });

      expect((await pending).status).toBe('rate_limited');
    });

    it('reads a 422 as a refusal in the server’s words, never as something waiting would fix', async () => {
      for (const code of ['idempotency.key_reused', 'media.generation.proposal.unprocessable']) {
        const pending = service.request('cozy-fall', { promptText: 'A crop.', avoidText: null, variantCount: 1 }, 'key-1');

        http.expectOne(BASE).flush(problem(code), { status: 422, statusText: 'Unprocessable Entity' });

        const outcome = await pending;
        expect(outcome.status).withContext(code).toBe('refused');
      }
    });

    it('reads anything else as unavailable, which is the one outcome a retry may replay', async () => {
      const pending = service.request('cozy-fall', { promptText: 'A crop.', avoidText: null, variantCount: 1 }, 'key-1');

      http.expectOne(BASE).error(new ProgressEvent('network'));

      expect((await pending).status).toBe('unavailable');
    });

  });

  describe('watch', () => {
    it('reads the run and the pictures it has produced so far', async () => {
      const pending = firstValueFrom(service.watch('cozy-fall', OPERATION_ID));

      const request = http.expectOne(`${BASE}/operations/${OPERATION_ID}`);
      expect(request.request.method).toBe('GET');
      expect(request.request.withCredentials).toBeTrue();

      request.flush(operationBody({ status: 'Running', stagedCount: 1, images: [stagedRow()] }));

      const outcome = await pending;
      expect(outcome.status).toBe('found');
      expect(outcome.status === 'found' && outcome.operation.images.length).toBe(1);
    });

    it('answers an unknown run and another workspace’s run alike, so neither discloses the other', async () => {
      const unknown = firstValueFrom(service.watch('cozy-fall', OPERATION_ID));
      http
        .expectOne(`${BASE}/operations/${OPERATION_ID}`)
        .flush(problem('media.generation_operation.not_found'), { status: 404, statusText: 'Not Found' });

      const elsewhere = firstValueFrom(service.watch('other-kitchen', OPERATION_ID));
      http
        .expectOne(`${GATEWAY}/api/v1/workspaces/other-kitchen/generated-images/operations/${OPERATION_ID}`)
        .flush(problem('media.generation_operation.not_found'), { status: 404, statusText: 'Not Found' });

      expect(await unknown).toEqual({ status: 'not_found' });
      expect(await elsewhere).toEqual({ status: 'not_found' });
    });

    it('never errors, so one failed read cannot tear down a polling loop', async () => {
      const pending = firstValueFrom(service.watch('cozy-fall', OPERATION_ID));
      http.expectOne(`${BASE}/operations/${OPERATION_ID}`).error(new ProgressEvent('network'));

      expect(await pending).toEqual({ status: 'unavailable' });
    });

    it('reads a 403 as forbidden rather than as an outage', async () => {
      const pending = firstValueFrom(service.watch('cozy-fall', OPERATION_ID));
      http
        .expectOne(`${BASE}/operations/${OPERATION_ID}`)
        .flush(problem('media.generation.forbidden'), { status: 403, statusText: 'Forbidden' });

      expect(await pending).toEqual({ status: 'forbidden' });
    });

    it('answers unavailable for a body it cannot read, rather than inventing a run', async () => {
      const pending = firstValueFrom(service.watch('cozy-fall', OPERATION_ID));
      http.expectOne(`${BASE}/operations/${OPERATION_ID}`).flush({ id: OPERATION_ID });

      expect(await pending).toEqual({ status: 'unavailable' });
    });
  });

  describe('preview', () => {
    it('asks for the bytes themselves, which is the only way the page’s own policy lets it show one', async () => {
      const pending = firstValueFrom(service.preview('cozy-fall', IMAGE_ID));

      const request = http.expectOne(`${BASE}/${IMAGE_ID}/preview`);
      expect(request.request.method).toBe('GET');
      expect(request.request.responseType).toBe('blob');
      expect(request.request.withCredentials).toBeTrue();

      request.flush(new Blob(['bytes'], { type: 'image/png' }));

      const outcome = await pending;
      expect(outcome.status).toBe('found');
      expect(outcome.status === 'found' && outcome.bytes.type).toBe('image/png');
    });

    it('reads the 503 as worth retrying, which is what the route documents it as', async () => {
      const pending = firstValueFrom(service.preview('cozy-fall', IMAGE_ID));
      http
        .expectOne(`${BASE}/${IMAGE_ID}/preview`)
        .flush(problemBlob('media.staged_image.unavailable'), { status: 503, statusText: 'Service Unavailable' });

      expect(await pending).toEqual({ status: 'unavailable' });
    });

    it('reads the 404 as gone, which covers unknown, another workspace’s, and already collected', async () => {
      const pending = firstValueFrom(service.preview('cozy-fall', IMAGE_ID));
      http
        .expectOne(`${BASE}/${IMAGE_ID}/preview`)
        .flush(problemBlob('media.staged_image.not_found'), { status: 404, statusText: 'Not Found' });

      expect(await pending).toEqual({ status: 'gone' });
    });
  });

  describe('reject', () => {
    it('declines one picture', async () => {
      const pending = service.reject('cozy-fall', IMAGE_ID);

      const request = http.expectOne(`${BASE}/${IMAGE_ID}`);
      expect(request.request.method).toBe('DELETE');
      expect(request.request.withCredentials).toBeTrue();

      request.flush(null, { status: 204, statusText: 'No Content' });

      expect((await pending).status).toBe('declined');
    });

    it('reads a repeat as declined too, so a half-finished tidy-up can simply be run again', async () => {
      const first = service.reject('cozy-fall', IMAGE_ID);
      http.expectOne(`${BASE}/${IMAGE_ID}`).flush(null, { status: 204, statusText: 'No Content' });
      expect((await first).status).toBe('declined');

      const second = service.reject('cozy-fall', IMAGE_ID);
      http.expectOne(`${BASE}/${IMAGE_ID}`).flush(null, { status: 204, statusText: 'No Content' });
      expect((await second).status).toBe('declined');
    });

    it('reads a kept or expired picture as a conflict, not as something to retry', async () => {
      const pending = service.reject('cozy-fall', IMAGE_ID);
      http
        .expectOne(`${BASE}/${IMAGE_ID}`)
        .flush(problem('media.staged_image.conflict'), { status: 409, statusText: 'Conflict' });

      expect((await pending).status).toBe('conflict');
    });

    it('tells not found and forbidden apart, because only one of them is about permission', async () => {
      const missing = service.reject('cozy-fall', IMAGE_ID);
      http
        .expectOne(`${BASE}/${IMAGE_ID}`)
        .flush(problem('media.staged_image.not_found'), { status: 404, statusText: 'Not Found' });
      expect((await missing).status).toBe('not_found');

      const refused = service.reject('cozy-fall', IMAGE_ID);
      http
        .expectOne(`${BASE}/${IMAGE_ID}`)
        .flush(problem('media.staged_image.forbidden'), { status: 403, statusText: 'Forbidden' });
      expect((await refused).status).toBe('forbidden');
    });
  });

  describe('the addresses it produces', () => {
    it('builds the download link and lets the server name the file', () => {
      expect(service.downloadUrl('cozy-fall', IMAGE_ID)).toBe(`${BASE}/${IMAGE_ID}/content`);
    });

    it('names the copy a download or a preview asks for, and nothing else, in the address', async () => {
      expect(service.downloadUrl('cozy-fall', IMAGE_ID, 'original')).toBe(`${BASE}/${IMAGE_ID}/content?rendition=original`);
      expect(service.downloadUrl('cozy-fall', IMAGE_ID, 'web')).toBe(`${BASE}/${IMAGE_ID}/content?rendition=web`);

      const pending = firstValueFrom(service.preview('cozy-fall', IMAGE_ID, 'thumbnail'));
      const request = http.expectOne(`${BASE}/${IMAGE_ID}/preview?rendition=thumbnail`);
      expect(request.request.withCredentials).toBeTrue();
      request.flush(new Blob(['bytes'], { type: 'image/jpeg' }));

      expect((await pending).status).toBe('found');
    });

    it('produces nothing but the gateway’s own route, a workspace slug and an id', () => {
      const urls = [service.downloadUrl('cozy-fall', IMAGE_ID)].filter((url): url is string => url !== null);

      for (const url of urls) {
        expect(url.startsWith(`${GATEWAY}/api/v1/workspaces/`)).withContext(url).toBeTrue();
        // No provider host, no storage container, no object key, no signature of any kind.
        expect(url).not.toMatch(/blob\.core|amazonaws|openai|azure|sig=|\?/);
      }
    });

    it('escapes what it puts in a path, so a slug can never widen the route', () => {
      expect(service.downloadUrl('a/b', IMAGE_ID)).toBe(
        `${GATEWAY}/api/v1/workspaces/a%2Fb/generated-images/${IMAGE_ID}/content`,
      );
    });
  });
});

/**
 * The degraded case, which needs its own module: the suite above loads a gateway address, and this is about
 * what happens when there is not one to load.
 */
describe('GeneratedImageService without a gateway address', () => {
  let service: GeneratedImageService;
  let http: HttpTestingController;

  beforeEach(async () => {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);

    const runtimeConfig = TestBed.inject(RuntimeConfigService);
    const loading = runtimeConfig.load();
    http.expectOne('/runtime-config.json').flush(null, { status: 500, statusText: 'Server Error' });
    await loading;

    service = TestBed.inject(GeneratedImageService);
  });

  afterEach(() => http.verify());

  it('asks for nothing and says so, rather than building a relative URL that would miss the gateway', async () => {
    const asked = await service.request(
      'cozy-fall',
      { promptText: 'A crop.', avoidText: null, variantCount: 1 },
      'key-1',
    );
    const declined = await service.reject('cozy-fall', IMAGE_ID);

    expect(asked.status).toBe('unavailable');
    expect(declined.status).toBe('unavailable');
    expect(await firstValueFrom(service.watch('cozy-fall', OPERATION_ID))).toEqual({ status: 'unavailable' });
    expect(await firstValueFrom(service.preview('cozy-fall', IMAGE_ID))).toEqual({ status: 'unavailable' });
  });

  it('offers no download link, so nothing renders an address that cannot work', () => {
    expect(service.downloadUrl('cozy-fall', IMAGE_ID)).toBeNull();
  });
});
