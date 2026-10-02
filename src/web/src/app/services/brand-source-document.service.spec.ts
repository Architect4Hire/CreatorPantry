import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

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
  currentVersion: { id: 'v', versionNumber: 1, mediaType: 'text/plain', sizeBytes: '42', originalFileName: 'Lasagna post.txt', createdAt: 'x' },
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
});
