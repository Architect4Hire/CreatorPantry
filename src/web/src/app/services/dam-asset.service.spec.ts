import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';

import { RuntimeConfigService } from '../core/runtime-config.service';
import { DamAssetSearchQuery } from '../models/dam-asset.models';
import { DamAssetService } from './dam-asset.service';

const GATEWAY = 'https://gateway.example';
const BASE = `${GATEWAY}/api/v1/workspaces/cozy-fall/dam-assets`;
const ID = '0f4b2c9a-1111-4222-8333-444455556666';

const FIRST: DamAssetSearchQuery = { search: '', channel: null, day: null, sort: 'RecentlyAdded', cursor: null };

function summaryBody(): Record<string, unknown> {
  return {
    id: ID,
    title: 'Soda bread hero',
    description: null,
    kind: 'Original',
    altText: null,
    channelKey: null,
    platformKey: null,
    day: null,
    styleKey: null,
    cuisineId: null,
    courseId: null,
    currentVersionNumber: 1,
    mediaType: 'image/jpeg',
    width: 1600,
    height: 1200,
    sizeBytes: 204800,
    utilizationCount: 0,
    createdAt: '2026-10-08T12:00:00+00:00',
    updatedAt: '2026-10-08T12:00:00+00:00',
  };
}

function problem(code: string): Record<string, unknown> {
  return { code, title: 'That could not be done.', traceId: 'trace-1' };
}

/** A ProblemDetails as the bytes a `responseType: 'blob'` request receives it as. */
function problemBlob(code: string): Blob {
  return new Blob([JSON.stringify(problem(code))], { type: 'application/problem+json' });
}

describe('DamAssetService', () => {
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

  describe('search', () => {
    it('asks the workspace route for the first page with credentials and no parameters', async () => {
      const pending = firstValueFrom(service.search('cozy-fall', FIRST));

      const request = http.expectOne(BASE);
      expect(request.request.method).toBe('GET');
      expect(request.request.withCredentials).toBeTrue();
      expect(request.request.params.keys()).toEqual([]);
      request.flush({ items: [summaryBody()], nextCursor: 'c1', totalCount: 40 });

      const outcome = await pending;
      expect(outcome.status).toBe('found');
      if (outcome.status === 'found') {
        expect(outcome.page.items[0].id).toBe(ID);
        expect(outcome.page.nextCursor).toBe('c1');
        expect(outcome.page.totalCount).toBe(40);
      }
    });

    it('sends filters, ordering and cursor as the route names them, and never a workspace', async () => {
      const pending = firstValueFrom(
        service.search('cozy-fall', { search: 'bread', channel: 'instagram', day: 'Sunday', sort: 'Title', cursor: 'c1' }),
      );

      const request = http.expectOne((candidate) => candidate.url === BASE);
      expect(request.request.params.get('search')).toBe('bread');
      expect(request.request.params.get('channel')).toBe('instagram');
      expect(request.request.params.get('day')).toBe('Sunday');
      expect(request.request.params.get('sort')).toBe('Title');
      expect(request.request.params.get('cursor')).toBe('c1');
      expect(request.request.params.get('includeTotal')).toBe('false');
      expect(request.request.params.has('workspaceId')).toBeFalse();
      request.flush({ items: [], nextCursor: null, totalCount: null });

      expect((await pending).status).toBe('found');
    });

    it('tells a stale cursor apart from a filter the server could not read', async () => {
      const stale = firstValueFrom(service.search('cozy-fall', { ...FIRST, cursor: 'old' }));
      http
        .expectOne((candidate) => candidate.url === BASE)
        .flush(problem('media.asset_search.cursor_invalid_request'), { status: 400, statusText: 'Bad Request' });
      expect(await stale).toEqual({ status: 'cursor_expired' });

      const rejected = firstValueFrom(service.search('cozy-fall', FIRST));
      http.expectOne(BASE).flush(problem('media.asset_search.invalid_request'), { status: 400, statusText: 'Bad Request' });
      expect(await rejected).toEqual({ status: 'invalid_request' });
    });

    it('reports any other failure, and an unreadable body, as unavailable', async () => {
      const failed = firstValueFrom(service.search('cozy-fall', FIRST));
      http.expectOne(BASE).flush(problem('server_error'), { status: 500, statusText: 'Server Error' });
      expect(await failed).toEqual({ status: 'unavailable' });

      const unreadable = firstValueFrom(service.search('cozy-fall', FIRST));
      http.expectOne(BASE).flush({ items: [{ nope: true }], nextCursor: null, totalCount: 1 });
      expect(await unreadable).toEqual({ status: 'unavailable' });
    });

    it('encodes the slug into the path', async () => {
      const pending = firstValueFrom(service.search('a/b', FIRST));

      http.expectOne(`${GATEWAY}/api/v1/workspaces/a%2Fb/dam-assets`).flush({ items: [], nextCursor: null, totalCount: 0 });

      expect((await pending).status).toBe('found');
    });
  });

  describe('versionContent', () => {
    it('reads one numbered version as bytes from that version’s own route, with credentials', async () => {
      const pending = firstValueFrom(service.versionContent('cozy-fall', ID, 2));

      const request = http.expectOne(`${BASE}/${ID}/versions/2/download`);
      expect(request.request.method).toBe('GET');
      expect(request.request.withCredentials).toBeTrue();
      expect(request.request.responseType).toBe('blob');
      request.flush(new Blob(['bytes'], { type: 'image/jpeg' }));

      const outcome = await pending;
      expect(outcome.status).toBe('found');
      if (outcome.status === 'found') expect(outcome.bytes.type).toBe('image/jpeg');
    });

    it('answers gone for a 404 and unavailable for anything else', async () => {
      const gone = firstValueFrom(service.versionContent('cozy-fall', ID, 9));
      http
        .expectOne(`${BASE}/${ID}/versions/9/download`)
        .flush(problemBlob('media.asset.not_found'), { status: 404, statusText: 'Not Found' });
      expect(await gone).toEqual({ status: 'gone' });

      const failed = firstValueFrom(service.versionContent('cozy-fall', ID, 2));
      http
        .expectOne(`${BASE}/${ID}/versions/2/download`)
        .flush(problemBlob('media.asset.unavailable'), { status: 503, statusText: 'Service Unavailable' });
      expect(await failed).toEqual({ status: 'unavailable' });
    });
  });

  describe('content', () => {
    it("fetches the picture as bytes from the gateway's own render route, with credentials", async () => {
      const pending = firstValueFrom(service.content('cozy-fall', ID));

      const request = http.expectOne(`${BASE}/${ID}/content`);
      expect(request.request.method).toBe('GET');
      expect(request.request.withCredentials).toBeTrue();
      expect(request.request.responseType).toBe('blob');
      // Named by id on the workspace route: nothing in the address is a key, a container or a signature.
      expect(request.request.urlWithParams).not.toMatch(/blob\.|storage|container|sig=|\?/i);
      request.flush(new Blob(['bytes'], { type: 'image/jpeg' }));

      const outcome = await pending;
      expect(outcome.status).toBe('found');
      if (outcome.status === 'found') expect(outcome.bytes.type).toBe('image/jpeg');
    });

    it('answers gone for a 404 — unknown, another workspace\'s and removed are one case', async () => {
      const pending = firstValueFrom(service.content('cozy-fall', ID));

      http
        .expectOne(`${BASE}/${ID}/content`)
        .flush(problemBlob('media.asset.not_found'), { status: 404, statusText: 'Not Found' });

      expect(await pending).toEqual({ status: 'gone' });
    });

    it('answers unavailable when the bytes could not be read, which retrying can fix', async () => {
      const pending = firstValueFrom(service.content('cozy-fall', ID));

      http
        .expectOne(`${BASE}/${ID}/content`)
        .flush(problemBlob('media.asset.unavailable'), { status: 503, statusText: 'Service Unavailable' });

      expect(await pending).toEqual({ status: 'unavailable' });
    });
  });
});
