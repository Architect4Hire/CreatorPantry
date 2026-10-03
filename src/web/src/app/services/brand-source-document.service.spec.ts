import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';

import { RuntimeConfigService } from '../core/runtime-config.service';
import { BrandSourceDocumentService } from './brand-source-document.service';

const BASE = 'https://gateway.example/api/v1/workspaces/w/brand-source-documents';

const DOC = {
  id: '11111111-1111-1111-1111-111111111111',
  title: 'Lasagna post',
  documentType: 'WritingSample',
  purpose: 'Voice',
  channelKey: null,
  audience: null,
  tags: [],
  status: 'Active',
  currentVersion: {
    id: 'v',
    versionNumber: 1,
    mediaType: 'text/plain',
    sizeBytes: '42',
    originalFileName: 'Lasagna post.txt',
    contentChecksum: 'sha256:abc',
    createdAt: 'x',
  },
  extraction: { state: 'Succeeded', origin: 'Extracted', at: 'x' },
  archivedAt: null,
  createdAt: 'x',
  updatedAt: 'x',
  concurrencyToken: 't',
};

describe('BrandSourceDocumentService', () => {
  let service: BrandSourceDocumentService;
  let http: HttpTestingController;

  beforeEach(async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);
    const loading = TestBed.inject(RuntimeConfigService).load();
    http.expectOne('/runtime-config.json').flush({ gatewayUrl: 'https://gateway.example' });
    await loading;
    service = TestBed.inject(BrandSourceDocumentService);
  });

  afterEach(() => http.verify());

  const describe_ = { title: 'Lasagna post', documentType: 'WritingSample', purpose: 'Voice' } as const;

  it('lists the workspace documents and decodes them, skipping any it cannot read', async () => {
    const result = service.list('w', 25);
    const req = http.expectOne((r) => r.url === BASE);
    expect(req.request.params.get('limit')).toBe('25');
    expect(req.request.withCredentials).toBeTrue();
    req.flush({ items: [DOC, { nonsense: true }], nextCursor: null });
    const outcome = await result;
    expect(outcome.status).toBe('ok');
    if (outcome.status === 'ok') {
      expect(outcome.page.items.length).toBe(1);
      expect(outcome.page.items[0]).toEqual(jasmine.objectContaining({ fileName: 'Lasagna post.txt', sizeBytes: 42 }));
    }
  });

  it('answers not_found for an unknown workspace and unavailable for anything else', async () => {
    let result = service.list('w');
    http.expectOne((r) => r.url === BASE).flush({}, { status: 404, statusText: 'x' });
    expect((await result).status).toBe('not_found');
    result = service.list('w');
    http.expectOne((r) => r.url === BASE).flush({}, { status: 500, statusText: 'x' });
    expect((await result).status).toBe('unavailable');
  });

  it('uploads a file as multipart with the idempotency key and no workspace field', async () => {
    const file = new File(['hello'], 'Lasagna post.txt', { type: 'text/plain' });
    const result = service.upload('w', file, { ...describe_, audience: 'Home cooks' }, 'key-1');
    const req = http.expectOne(BASE);
    expect(req.request.method).toBe('POST');
    expect(req.request.headers.get('Idempotency-Key')).toBe('key-1');
    const form = req.request.body as FormData;
    expect(form.get('title')).toBe('Lasagna post');
    expect(form.get('documentType')).toBe('WritingSample');
    expect(form.get('purpose')).toBe('Voice');
    expect(form.get('audience')).toBe('Home cooks');
    expect(form.get('file')).toBeInstanceOf(File);
    expect(Array.from(form.keys()).some((k) => /workspace/i.test(k))).toBeFalse();
    req.flush(DOC, { status: 201, statusText: 'Created' });
    const outcome = await result;
    expect(outcome).toEqual({ status: 'added', document: jasmine.objectContaining({ id: DOC.id }), replayed: false });
  });

  it('reports a replay when the server says it already had this request', async () => {
    const result = service.upload('w', new File(['x'], 'a.txt'), describe_, 'key-1');
    http.expectOne(BASE).flush(DOC, { status: 201, statusText: 'Created', headers: { 'Idempotent-Replayed': 'true' } });
    const outcome = await result;
    expect(outcome.status === 'added' && outcome.replayed).toBeTrue();
  });

  it('sends pasted text as JSON to the text route, without a workspace field', async () => {
    const result = service.pasteText('w', { ...describe_, text: 'Trust me.' }, 'key-2');
    const req = http.expectOne(`${BASE}/text`);
    expect(req.request.headers.get('Idempotency-Key')).toBe('key-2');
    expect(req.request.body).toEqual({ title: 'Lasagna post', documentType: 'WritingSample', purpose: 'Voice', text: 'Trust me.' });
    req.flush(DOC, { status: 201, statusText: 'Created' });
    expect((await result).status).toBe('added');
  });

  const cases: readonly [number, object, string][] = [
    [422, { code: 'brand.source.file.unsupported.unprocessable' }, 'unsupported'],
    [413, { code: 'brand.source.file.payload_too_large' }, 'too_large'],
    [413, {}, 'too_large'],
    [422, { code: 'brand.source.file.corrupt.unprocessable' }, 'corrupt'],
    [422, { code: 'brand.source.file.rejected.unprocessable' }, 'rejected'],
    [503, { code: 'brand.source.scan.unavailable' }, 'unavailable'],
    [503, { code: 'brand.source.storage.unavailable' }, 'unavailable'],
    [422, { code: 'idempotency.key_reused' }, 'key_reused'],
    [400, { code: 'brand.source.invalid_request' }, 'invalid'],
    [403, {}, 'forbidden'],
    [404, {}, 'not_found'],
    [500, {}, 'unavailable'],
  ];

  for (const [status, body, reason] of cases) {
    it(`maps ${status} ${JSON.stringify(body)} to ${reason}`, async () => {
      const result = service.upload('w', new File(['x'], 'a.txt'), describe_, 'k');
      http.expectOne(BASE).flush(body, { status, statusText: 'x' });
      expect(await result).toEqual({ status: 'failed', reason: reason as never });
    });
  }

  it('reads one document with the token the next archive must quote', async () => {
    const result = service.get('w', DOC.id);
    const req = http.expectOne(`${BASE}/${DOC.id}`);
    expect(req.request.withCredentials).toBeTrue();
    req.flush(DOC);
    const outcome = await result;
    expect(outcome.status === 'ok' && outcome.document.versionNumber === 1 && outcome.document.concurrencyToken === 't').toBeTrue();
  });

  it('answers not_found or unavailable when a document cannot be read', async () => {
    let result = service.get('w', DOC.id);
    http.expectOne(`${BASE}/${DOC.id}`).flush({}, { status: 404, statusText: 'x' });
    expect((await result).status).toBe('not_found');
    result = service.get('w', DOC.id);
    http.expectOne(`${BASE}/${DOC.id}`).flush({}, { status: 500, statusText: 'x' });
    expect((await result).status).toBe('unavailable');
  });

  it('puts a document on the shelf and brings it back, quoting the token and sending no workspace field', async () => {
    let result = service.archive('w', DOC.id, 't');
    let req = http.expectOne(`${BASE}/${DOC.id}/archive`);
    expect(req.request.body).toEqual({ expectedConcurrencyToken: 't' });
    req.flush({ ...DOC, status: 'Archived', concurrencyToken: 't2' });
    const archived = await result;
    expect(archived.status === 'done' && archived.document.status === 'Archived' && archived.document.concurrencyToken === 't2').toBeTrue();

    result = service.unarchive('w', DOC.id, 't2');
    req = http.expectOne(`${BASE}/${DOC.id}/unarchive`);
    expect(req.request.body).toEqual({ expectedConcurrencyToken: 't2' });
    req.flush(DOC);
    expect((await result).status).toBe('done');
  });

  for (const [status, expected] of [[409, 'conflict'], [403, 'forbidden'], [404, 'not_found'], [500, 'unavailable']] as const) {
    it(`maps a ${status} from the shelf to ${expected}`, async () => {
      const result = service.archive('w', DOC.id, 't');
      http.expectOne(`${BASE}/${DOC.id}/archive`).flush({}, { status, statusText: 'x' });
      expect((await result).status).toBe(expected);
    });
  }

  it('treats a response it cannot read as unavailable', async () => {
    const result = service.upload('w', new File(['x'], 'a.txt'), describe_, 'k');
    http.expectOne(BASE).flush({ nonsense: true }, { status: 201, statusText: 'Created' });
    expect(await result).toEqual({ status: 'failed', reason: 'unavailable' });
  });

  it('answers cancelled when aborted, before or during the request', async () => {
    const before = new AbortController();
    before.abort();
    expect(await service.upload('w', new File(['x'], 'a.txt'), describe_, 'k', before.signal)).toEqual({ status: 'failed', reason: 'cancelled' });

    const during = new AbortController();
    const result = service.upload('w', new File(['x'], 'a.txt'), describe_, 'k', during.signal);
    const req = http.expectOne(BASE);
    during.abort();
    expect(await result).toEqual({ status: 'failed', reason: 'cancelled' });
    expect(req.cancelled).toBeTrue();
  });

  // ---- The Brand Library list ----

  describe('searchLibrary', () => {
    /** A row with no extraction: this client cannot say what state it is in, so it does not guess. */
    const UNREADABLE = { ...DOC, extraction: undefined };

    const ALL_FILTERS = {
      search: '  house  ',
      status: 'Archived',
      documentType: 'StyleGuide',
      purpose: 'WritingStyle',
      channelKey: 'instagram',
      extractionState: 'Failed',
      cursor: 'abc==',
      limit: 50,
    } as const;

    it('sends every filter in force, trimming the search term', async () => {
      const result = firstValueFrom(service.searchLibrary('w', ALL_FILTERS));
      const req = http.expectOne((r) => r.url === BASE);
      const params = req.request.params;

      expect(params.get('search')).toBe('house');
      expect(params.get('status')).toBe('Archived');
      expect(params.get('documentType')).toBe('StyleGuide');
      expect(params.get('purpose')).toBe('WritingStyle');
      expect(params.get('channelKey')).toBe('instagram');
      expect(params.get('extractionState')).toBe('Failed');
      expect(params.get('limit')).toBe('50');

      // Verbatim: a re-encoded cursor is a different position, and the server refuses one it did not issue.
      expect(params.get('cursor')).toBe('abc==');
      expect(req.request.withCredentials).toBeTrue();

      req.flush({ items: [], nextCursor: null });
      await result;
    });

    it('leaves out the filters that are not set, rather than sending empty values', async () => {
      const result = firstValueFrom(
        service.searchLibrary('w', {
          search: '   ',
          status: 'Active',
          documentType: null,
          purpose: null,
          channelKey: null,
          extractionState: null,
          cursor: null,
          limit: 25,
        }),
      );

      const req = http.expectOne((r) => r.url === BASE);
      expect(req.request.params.keys().sort()).toEqual(['limit', 'status']);

      req.flush({ items: [], nextCursor: null });
      await result;
    });

    it('decodes a page and leaves out a row it cannot read', async () => {
      const result = firstValueFrom(service.searchLibrary('w', ALL_FILTERS));

      http.expectOne((r) => r.url === BASE).flush({ items: [DOC, UNREADABLE], nextCursor: 'next==' });

      const outcome = await result;
      expect(outcome.status).toBe('ok');
      if (outcome.status !== 'ok') return;

      expect(outcome.page.items.length).toBe(1);
      expect(outcome.page.items[0]).toEqual(
        jasmine.objectContaining({
          title: 'Lasagna post',
          versionNumber: 1,
          extraction: { state: 'Succeeded', origin: 'Extracted', at: 'x' },
        }),
      );
      expect(outcome.page.nextCursor).toBe('next==');
    });

    it('tells a refused cursor apart from a refused filter, because the remedies differ', async () => {
      let result = firstValueFrom(service.searchLibrary('w', ALL_FILTERS));
      http
        .expectOne((r) => r.url === BASE)
        .flush(
          { code: 'brand.source.invalid_request', errors: { cursor: ['Start the list again without a cursor.'] } },
          { status: 400, statusText: 'Bad Request' },
        );
      expect((await result).status).toBe('cursor_expired');

      result = firstValueFrom(service.searchLibrary('w', ALL_FILTERS));
      http
        .expectOne((r) => r.url === BASE)
        .flush(
          { code: 'brand.source.invalid_request', errors: { purpose: ['Use one of: Voice, WritingStyle.'] } },
          { status: 400, statusText: 'Bad Request' },
        );
      expect((await result).status).toBe('invalid_request');
    });

    it('reports anything else as unavailable, including a body it cannot read', async () => {
      let result = firstValueFrom(service.searchLibrary('w', ALL_FILTERS));
      http.expectOne((r) => r.url === BASE).flush({}, { status: 404, statusText: 'Not Found' });
      expect((await result).status).toBe('unavailable');

      result = firstValueFrom(service.searchLibrary('w', ALL_FILTERS));
      http.expectOne((r) => r.url === BASE).flush({ nonsense: true });
      expect((await result).status).toBe('unavailable');
    });
  });
});
