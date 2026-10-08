import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';

import { RuntimeConfigService } from '../core/runtime-config.service';
import { DamAssetService } from './dam-asset.service';

const GATEWAY = 'https://gateway.example';
const BASE = `${GATEWAY}/api/v1/workspaces/cozy-fall/dam-assets`;
const ID = '0f4b2c9a-1111-4222-8333-444455556666';

function detailBody(): Record<string, unknown> {
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
    concurrencyToken: 'AAAAAAAAB9E=',
  };
}

function useBody(): Record<string, unknown> {
  return {
    id: 'u1',
    platformKey: 'instagram',
    utilizedOn: '2026-10-06',
    utilizedDay: 'Tuesday',
    campaignName: null,
    notes: null,
    createdAt: '2026-10-07T01:00:00+00:00',
  };
}

function problem(code: string): Record<string, unknown> {
  return { code, title: 'That could not be done.', traceId: 'trace-1' };
}

describe('DamAssetService — one asset', () => {
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

  describe('detail', () => {
    it('reads one asset by id, with credentials, and never asks for a removed one', async () => {
      const pending = firstValueFrom(service.detail('cozy-fall', ID));

      const request = http.expectOne(`${BASE}/${ID}`);
      expect(request.request.method).toBe('GET');
      expect(request.request.withCredentials).toBeTrue();
      expect(request.request.params.has('includeDeleted')).toBeFalse();
      request.flush(detailBody());

      const outcome = await pending;
      expect(outcome.status).toBe('found');
      if (outcome.status === 'found') expect(outcome.asset.title).toBe('Soda bread hero');
    });

    it("answers not_found for a 404 — unknown, another workspace's and removed are one case", async () => {
      const pending = firstValueFrom(service.detail('cozy-fall', ID));

      http.expectOne(`${BASE}/${ID}`).flush(problem('media.asset.not_found'), { status: 404, statusText: 'Not Found' });

      expect(await pending).toEqual({ status: 'not_found' });
    });

    it('answers unavailable for a failure or an unreadable body', async () => {
      const failed = firstValueFrom(service.detail('cozy-fall', ID));
      http.expectOne(`${BASE}/${ID}`).flush(null, { status: 503, statusText: 'Unavailable' });
      expect(await failed).toEqual({ status: 'unavailable' });

      const unreadable = firstValueFrom(service.detail('cozy-fall', ID));
      http.expectOne(`${BASE}/${ID}`).flush({ id: ID });
      expect(await unreadable).toEqual({ status: 'unavailable' });
    });
  });

  describe('utilization', () => {
    it('asks for the first page with no parameters, so the total comes with it', async () => {
      const pending = firstValueFrom(service.utilization('cozy-fall', ID, null));

      const request = http.expectOne(`${BASE}/${ID}/utilization`);
      expect(request.request.method).toBe('GET');
      expect(request.request.withCredentials).toBeTrue();
      expect(request.request.params.keys()).toEqual([]);
      request.flush({ items: [useBody()], nextCursor: 'c1', totalCount: 9 });

      const outcome = await pending;
      expect(outcome.status).toBe('found');
      if (outcome.status === 'found') {
        expect(outcome.page.items[0].utilizedOn).toBe('2026-10-06');
        expect(outcome.page.totalCount).toBe(9);
      }
    });

    it('follows a cursor without asking to be counted again', async () => {
      const pending = firstValueFrom(service.utilization('cozy-fall', ID, 'c1'));

      const request = http.expectOne((candidate) => candidate.url === `${BASE}/${ID}/utilization`);
      expect(request.request.params.get('cursor')).toBe('c1');
      expect(request.request.params.get('includeTotal')).toBe('false');
      request.flush({ items: [], nextCursor: null, totalCount: null });

      expect((await pending).status).toBe('found');
    });

    it('tells a stale cursor, a missing asset and a failure apart', async () => {
      const url = `${BASE}/${ID}/utilization`;

      const stale = firstValueFrom(service.utilization('cozy-fall', ID, 'old'));
      http
        .expectOne((candidate) => candidate.url === url)
        .flush(problem('media.asset_search.cursor_invalid_request'), { status: 400, statusText: 'Bad Request' });
      expect(await stale).toEqual({ status: 'cursor_expired' });

      const missing = firstValueFrom(service.utilization('cozy-fall', ID, null));
      http.expectOne(url).flush(problem('media.asset.not_found'), { status: 404, statusText: 'Not Found' });
      expect(await missing).toEqual({ status: 'not_found' });

      const failed = firstValueFrom(service.utilization('cozy-fall', ID, null));
      http.expectOne(url).flush(null, { status: 500, statusText: 'Server Error' });
      expect(await failed).toEqual({ status: 'unavailable' });
    });
  });

  describe('download links', () => {
    it("are the gateway's own download routes, named by id and version number", () => {
      expect(service.downloadUrl('cozy-fall', ID)).toBe(`${BASE}/${ID}/download`);
      expect(service.versionDownloadUrl('cozy-fall', ID, 3)).toBe(`${BASE}/${ID}/versions/3/download`);
    });

    it('carry no file name, query, signature or storage location of their own', () => {
      for (const url of [service.downloadUrl('cozy-fall', ID), service.versionDownloadUrl('cozy-fall', ID, 3)]) {
        expect(url).not.toContain('?');
        expect(url).not.toMatch(/\.(jpe?g|png|webp)$/i);
        expect(url).not.toMatch(/blob\.|storage|container|sig=/i);
      }
    });
  });
});
