import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';

import { RuntimeConfigService } from '../core/runtime-config.service';
import { PromptLibraryService } from './prompt-library.service';

const GATEWAY = 'https://gateway.example';
const BASE = `${GATEWAY}/api/v1/workspaces/cozy-fall/prompts`;
const ID = '0f4b2c9a-1111-4222-8333-444455556666';

function summaryBody(): Record<string, unknown> {
  return {
    promptRecordId: ID,
    channelKey: 'instagram',
    imageKind: 'Hero',
    textPreview: 'A tight crop',
    textLength: 12,
    label: null,
    source: 'Manual',
    recipeId: null,
    recipeVersionId: null,
    createdAt: '2026-10-08T12:00:00+00:00',
  };
}

function detailBody(): Record<string, unknown> {
  return {
    promptRecordId: ID,
    channelKey: 'instagram',
    imageKind: 'Hero',
    text: 'A tight crop',
    generatedText: null,
    label: null,
    source: 'Manual',
    aiProposalId: null,
    recipeId: null,
    recipeVersionId: null,
    promptTemplateId: null,
    promptTemplateVersion: null,
    promptTemplateBodyChecksum: null,
    createdAt: '2026-10-08T12:00:00+00:00',
  };
}

function problem(code: string): Record<string, unknown> {
  return { code, title: 'That could not be done.', traceId: 'trace-1' };
}

describe('PromptLibraryService', () => {
  let service: PromptLibraryService;
  let http: HttpTestingController;

  beforeEach(async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);

    const runtimeConfig = TestBed.inject(RuntimeConfigService);
    const loading = runtimeConfig.load();
    http.expectOne('/runtime-config.json').flush({ gatewayUrl: GATEWAY });
    await loading;

    service = TestBed.inject(PromptLibraryService);
  });

  afterEach(() => http.verify());

  describe('search', () => {
    it('asks the workspace route for the first page with credentials and no parameters', async () => {
      const pending = firstValueFrom(service.search('cozy-fall', { search: '', channel: null, cursor: null }));

      const request = http.expectOne(BASE);
      expect(request.request.method).toBe('GET');
      expect(request.request.withCredentials).toBeTrue();
      expect(request.request.params.keys()).toEqual([]);
      request.flush({ items: [summaryBody()], nextCursor: 'c1', totalCount: 3 });

      const outcome = await pending;
      expect(outcome.status).toBe('found');
      if (outcome.status === 'found') {
        expect(outcome.page.items[0].promptRecordId).toBe(ID);
        expect(outcome.page.nextCursor).toBe('c1');
        expect(outcome.page.totalCount).toBe(3);
      }
    });

    it('sends search, channel and cursor as the route names them, and never a workspace or a sort', async () => {
      const pending = firstValueFrom(service.search('cozy-fall', { search: 'chili', channel: 'instagram', cursor: 'c1' }));

      const request = http.expectOne((candidate) => candidate.url === BASE);
      expect(request.request.params.get('search')).toBe('chili');
      expect(request.request.params.get('channel')).toBe('instagram');
      expect(request.request.params.get('cursor')).toBe('c1');
      expect(request.request.params.get('includeTotal')).toBe('false');
      expect(request.request.params.has('sort')).toBeFalse();
      expect(request.request.params.has('workspaceId')).toBeFalse();
      request.flush({ items: [], nextCursor: null, totalCount: null });

      expect((await pending).status).toBe('found');
    });

    it('reports a stale cursor as its own outcome, because its remedy is to start again', async () => {
      const pending = firstValueFrom(service.search('cozy-fall', { search: '', channel: null, cursor: 'old' }));

      http
        .expectOne((candidate) => candidate.url === BASE)
        .flush(problem('content.prompt.cursor.invalid'), { status: 400, statusText: 'Bad Request' });

      expect(await pending).toEqual({ status: 'cursor_expired' });
    });

    it('reports any other failure, and an unreadable body, as unavailable', async () => {
      const failed = firstValueFrom(service.search('cozy-fall', { search: '', channel: null, cursor: null }));
      http.expectOne(BASE).flush(problem('server_error'), { status: 500, statusText: 'Server Error' });
      expect(await failed).toEqual({ status: 'unavailable' });

      const unreadable = firstValueFrom(service.search('cozy-fall', { search: '', channel: null, cursor: null }));
      http.expectOne(BASE).flush({ items: [{ nope: true }], nextCursor: null, totalCount: 1 });
      expect(await unreadable).toEqual({ status: 'unavailable' });
    });

    it('encodes the slug into the path', async () => {
      const pending = firstValueFrom(service.search('a/b', { search: '', channel: null, cursor: null }));

      http.expectOne(`${GATEWAY}/api/v1/workspaces/a%2Fb/prompts`).flush({ items: [], nextCursor: null, totalCount: 0 });

      expect((await pending).status).toBe('found');
    });
  });

  describe('get', () => {
    it('reads one prompt in full', async () => {
      const pending = firstValueFrom(service.get('cozy-fall', ID));

      const request = http.expectOne(`${BASE}/${ID}`);
      expect(request.request.method).toBe('GET');
      expect(request.request.withCredentials).toBeTrue();
      request.flush(detailBody());

      const outcome = await pending;
      expect(outcome.status).toBe('found');
      if (outcome.status === 'found') expect(outcome.prompt.text).toBe('A tight crop');
    });

    it("answers not_found for a 404, whichever code it carries — unknown and another workspace's are one case", async () => {
      const module = firstValueFrom(service.get('cozy-fall', ID));
      http.expectOne(`${BASE}/${ID}`).flush(problem('content.prompt.not_found'), { status: 404, statusText: 'Not Found' });
      expect(await module).toEqual({ status: 'not_found' });

      const edge = firstValueFrom(service.get('cozy-fall', 'not-a-guid'));
      http.expectOne(`${BASE}/not-a-guid`).flush(problem('not_found'), { status: 404, statusText: 'Not Found' });
      expect(await edge).toEqual({ status: 'not_found' });
    });

    it('answers unavailable for a failure or an unreadable body', async () => {
      const failed = firstValueFrom(service.get('cozy-fall', ID));
      http.expectOne(`${BASE}/${ID}`).flush(null, { status: 503, statusText: 'Unavailable' });
      expect(await failed).toEqual({ status: 'unavailable' });

      const unreadable = firstValueFrom(service.get('cozy-fall', ID));
      http.expectOne(`${BASE}/${ID}`).flush({ promptRecordId: ID });
      expect(await unreadable).toEqual({ status: 'unavailable' });
    });
  });

  describe('download links', () => {
    it("are the gateway's own text and record routes, named by id", () => {
      expect(service.textDownloadUrl('cozy-fall', ID)).toBe(`${BASE}/${ID}/text`);
      expect(service.recordDownloadUrl('cozy-fall', ID)).toBe(`${BASE}/${ID}/record`);
    });

    it('carry no file name, query or storage location of their own', () => {
      for (const url of [service.textDownloadUrl('cozy-fall', ID), service.recordDownloadUrl('cozy-fall', ID)]) {
        expect(url).not.toContain('?');
        expect(url).not.toMatch(/\.(txt|json)$/);
        expect(url).not.toMatch(/blob|storage|container/i);
      }
    });
  });
});
