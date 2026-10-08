import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { RuntimeConfigService } from '../core/runtime-config.service';
import { DamAssetService, DamVersionUploadEvent } from './dam-asset.service';
import { WorkspaceTagService } from './workspace-tag.service';

const GATEWAY = 'https://gateway.example';
const BASE = `${GATEWAY}/api/v1/workspaces/cozy-fall/dam-assets`;
const ID = '0f4b2c9a-1111-4222-8333-444455556666';

function detailBody(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    id: ID,
    title: 'Soda bread hero',
    description: null,
    altText: null,
    kind: 'Original',
    channelKey: null,
    platformKey: null,
    day: null,
    styleKey: null,
    cuisineId: null,
    courseId: null,
    rightsHolder: null,
    attributionText: null,
    tags: [],
    currentVersion: null,
    versions: [],
    versionCount: 0,
    utilizationCount: 0,
    recipeLinkCount: 0,
    recipeLinks: [],
    prompts: [],
    deletedAt: null,
    deletedByMembershipId: null,
    createdAt: '2026-10-08T12:00:00+00:00',
    updatedAt: '2026-10-08T12:00:00+00:00',
    concurrencyToken: 'token-2',
    ...overrides,
  };
}

function versionBody(): Record<string, unknown> {
  return {
    versionNumber: 3,
    mediaType: 'image/png',
    width: 800,
    height: 600,
    sizeBytes: 1234,
    contentChecksum: 'ABC',
    originalFileName: 'new.png',
    source: 'Upload',
    sourceGeneratedImageId: null,
    createdAt: '2026-10-09T12:00:00+00:00',
  };
}

function problem(code: string, extra: Record<string, unknown> = {}): Record<string, unknown> {
  return { code, title: 'That could not be done.', traceId: 'trace-1', ...extra };
}

describe('DamAssetService — changing an asset', () => {
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

  describe('patchMetadata', () => {
    const body = { expectedConcurrencyToken: 'token-1', title: 'Better title', altText: null };

    it('sends the merge patch as given, with credentials and the idempotency key, and returns the asset', async () => {
      const pending = service.patchMetadata('cozy-fall', ID, body, 'key-1');

      const request = http.expectOne(`${BASE}/${ID}`);
      expect(request.request.method).toBe('PATCH');
      expect(request.request.withCredentials).toBeTrue();
      expect(request.request.headers.get('Idempotency-Key')).toBe('key-1');
      // Exactly what the caller built: a field it left out is not added, and a null it sent is not dropped.
      expect(request.request.body).toEqual(body);
      request.flush(detailBody({ title: 'Better title' }));

      const outcome = await pending;
      expect(outcome.status).toBe('updated');
      if (outcome.status === 'updated') {
        expect(outcome.asset.title).toBe('Better title');
        expect(outcome.asset.concurrencyToken).toBe('token-2');
      }
    });

    it('reports a stale token as its own outcome, because its remedy is to read again', async () => {
      const pending = service.patchMetadata('cozy-fall', ID, body, 'key-1');

      http.expectOne(`${BASE}/${ID}`).flush(problem('media.asset.stale.conflict'), { status: 409, statusText: 'Conflict' });

      expect(await pending).toEqual({ status: 'stale' });
    });

    it('does not dress any other conflict up as a stale one', async () => {
      const pending = service.patchMetadata('cozy-fall', ID, body, 'key-1');

      http.expectOne(`${BASE}/${ID}`).flush(problem('media.asset.conflict'), { status: 409, statusText: 'Conflict' });

      expect(await pending).toEqual({ status: 'unavailable' });
    });

    it("carries the server's field errors on a refused edit", async () => {
      const pending = service.patchMetadata('cozy-fall', ID, body, 'key-1');

      http
        .expectOne(`${BASE}/${ID}`)
        .flush(problem('media.asset.invalid_request', { title: 'That edit could not be saved.', errors: { title: ['A title is required.'] } }), {
          status: 400,
          statusText: 'Bad Request',
        });

      expect(await pending).toEqual({
        status: 'invalid',
        message: 'That edit could not be saved.',
        fieldErrors: { title: ['A title is required.'] },
      });
    });

    it('tells forbidden, not found and a failure apart', async () => {
      const forbidden = service.patchMetadata('cozy-fall', ID, body, 'k');
      http.expectOne(`${BASE}/${ID}`).flush(problem('media.asset.forbidden'), { status: 403, statusText: 'Forbidden' });
      expect(await forbidden).toEqual({ status: 'forbidden' });

      const missing = service.patchMetadata('cozy-fall', ID, body, 'k');
      http.expectOne(`${BASE}/${ID}`).flush(problem('media.asset.not_found'), { status: 404, statusText: 'Not Found' });
      expect(await missing).toEqual({ status: 'not_found' });

      const failed = service.patchMetadata('cozy-fall', ID, body, 'k');
      http.expectOne(`${BASE}/${ID}`).flush(null, { status: 503, statusText: 'Unavailable' });
      expect(await failed).toEqual({ status: 'unavailable' });
    });
  });

  describe('addVersion', () => {
    const file = new File(['png-bytes'], 'new.png', { type: 'image/png' });

    function collect(): { events: DamVersionUploadEvent[]; completed: () => boolean; cancel: () => void } {
      const events: DamVersionUploadEvent[] = [];
      let done = false;
      const subscription = service.addVersion('cozy-fall', ID, file, 'key-1').subscribe({
        next: (event) => events.push(event),
        complete: () => (done = true),
      });

      return { events, completed: () => done, cancel: () => subscription.unsubscribe() };
    }

    it('posts the file alone as multipart to the versions route, with credentials and the idempotency key', () => {
      const upload = collect();

      const request = http.expectOne(`${BASE}/${ID}/versions`);
      expect(request.request.method).toBe('POST');
      expect(request.request.withCredentials).toBeTrue();
      expect(request.request.headers.get('Idempotency-Key')).toBe('key-1');
      expect(request.request.reportProgress).toBeTrue();

      const form = request.request.body as FormData;
      // One field. No title, no alt text, no version number, no token: bytes only, and the server numbers it.
      expect(Array.from(form.keys())).toEqual(['file']);
      expect((form.get('file') as File).name).toBe('new.png');

      request.flush(versionBody(), { status: 201, statusText: 'Created' });
      expect(upload.completed()).toBeTrue();
    });

    it('reports how much has been sent, then the stored version', () => {
      const upload = collect();
      const request = http.expectOne(`${BASE}/${ID}/versions`);

      request.event({ type: 1, loaded: 250, total: 1000 });
      request.event({ type: 1, loaded: 1000, total: 1000 });
      request.flush(versionBody(), { status: 201, statusText: 'Created' });

      expect(upload.events[0]).toEqual({ kind: 'progress', percent: 25 });
      expect(upload.events[1]).toEqual({ kind: 'progress', percent: 100 });

      const last = upload.events[upload.events.length - 1];
      expect(last.kind).toBe('done');
      if (last.kind === 'done' && last.outcome.status === 'added') {
        expect(last.outcome.version.versionNumber).toBe(3);
      } else {
        fail('expected the stored version');
      }
    });

    it('reports progress as unknown, not zero, when the browser cannot say how far along it is', () => {
      const upload = collect();
      const request = http.expectOne(`${BASE}/${ID}/versions`);

      request.event({ type: 1, loaded: 250 });
      request.flush(versionBody(), { status: 201, statusText: 'Created' });

      expect(upload.events[0]).toEqual({ kind: 'progress', percent: null });
    });

    it('aborts the request when the caller unsubscribes', () => {
      const upload = collect();
      const request = http.expectOne(`${BASE}/${ID}/versions`);

      upload.cancel();

      expect(request.cancelled).toBeTrue();
      expect(upload.events.some((event) => event.kind === 'done')).toBeFalse();
    });

    const failures: readonly [number, string, string][] = [
      [422, 'media.asset.unprocessable', 'unsupported'],
      [413, 'payload_too_large', 'too_large'],
      [400, 'media.asset.invalid_request', 'invalid'],
      [403, 'media.asset.forbidden', 'forbidden'],
      [404, 'media.asset.not_found', 'not_found'],
      [409, 'media.asset.version_taken.conflict', 'version_taken'],
      [409, 'media.asset.conflict', 'unavailable'],
      [503, 'media.asset.unavailable', 'unavailable'],
    ];

    for (const [status, code, reason] of failures) {
      it(`ends with "${reason}" for a ${status} ${code}`, () => {
        const upload = collect();

        http.expectOne(`${BASE}/${ID}/versions`).flush(problem(code), { status, statusText: 'Refused' });

        expect(upload.events).toEqual([{ kind: 'done', outcome: { status: 'failed', reason } } as DamVersionUploadEvent]);
        expect(upload.completed()).toBeTrue();
      });
    }

    it('treats an unreadable success body as not stored, rather than inventing a version', () => {
      const upload = collect();

      http.expectOne(`${BASE}/${ID}/versions`).flush({ nope: true }, { status: 201, statusText: 'Created' });

      expect(upload.events).toEqual([{ kind: 'done', outcome: { status: 'failed', reason: 'unavailable' } }]);
    });
  });
});

describe('WorkspaceTagService', () => {
  let service: WorkspaceTagService;
  let http: HttpTestingController;
  const url = `${GATEWAY}/api/v1/workspaces/cozy-fall/tags`;

  beforeEach(async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);

    const runtimeConfig = TestBed.inject(RuntimeConfigService);
    const loading = runtimeConfig.load();
    http.expectOne('/runtime-config.json').flush({ gatewayUrl: GATEWAY });
    await loading;

    service = TestBed.inject(WorkspaceTagService);
  });

  afterEach(() => http.verify());

  it("reads the workspace's tags with credentials, from the workspace's own route", async () => {
    const pending = service.list('cozy-fall');

    const request = http.expectOne(url);
    expect(request.request.method).toBe('GET');
    expect(request.request.withCredentials).toBeTrue();
    request.flush([{ id: 't1', name: 'Weeknight' }]);

    expect(await pending).toEqual({ status: 'found', tags: [{ id: 't1', name: 'Weeknight' }] });
  });

  it('answers found with an empty list for a workspace that has no tags', async () => {
    const pending = service.list('cozy-fall');

    http.expectOne(url).flush([]);

    expect(await pending).toEqual({ status: 'found', tags: [] });
  });

  it('answers unavailable for a failure or an unreadable body', async () => {
    const failed = service.list('cozy-fall');
    http.expectOne(url).flush(null, { status: 500, statusText: 'Server Error' });
    expect(await failed).toEqual({ status: 'unavailable' });

    const unreadable = service.list('cozy-fall');
    http.expectOne(url).flush({ items: [] });
    expect(await unreadable).toEqual({ status: 'unavailable' });
  });

  it('encodes the slug into the path', async () => {
    const pending = service.list('a/b');

    http.expectOne(`${GATEWAY}/api/v1/workspaces/a%2Fb/tags`).flush([]);

    expect((await pending).status).toBe('found');
  });
});
