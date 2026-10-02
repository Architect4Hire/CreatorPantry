import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { RuntimeConfigService } from '../core/runtime-config.service';
import { BrandSourceExtractionService } from './brand-source-extraction.service';

const DOC = '11111111-1111-1111-1111-111111111111';
const URL = `https://gateway.example/api/v1/workspaces/w/brand-source-documents/${DOC}/versions/1/extraction`;

const SUCCEEDED = {
  versionNumber: 1,
  state: 'Succeeded',
  id: 'x1',
  ordinal: 1,
  origin: 'Extracted',
  text: 'Warm and plain.\n',
  reason: null,
  at: 'now',
  isReading: false,
};

describe('BrandSourceExtractionService', () => {
  let service: BrandSourceExtractionService;
  let http: HttpTestingController;

  beforeEach(async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);
    const loading = TestBed.inject(RuntimeConfigService).load();
    http.expectOne('/runtime-config.json').flush({ gatewayUrl: 'https://gateway.example' });
    await loading;
    service = TestBed.inject(BrandSourceExtractionService);
  });

  afterEach(() => http.verify());

  it('reads the text and decodes it, with the id a correction must quote', async () => {
    const result = service.get('w', DOC, 1);
    const req = http.expectOne(URL);
    expect(req.request.withCredentials).toBeTrue();
    req.flush(SUCCEEDED);
    const outcome = await result;
    expect(outcome.status).toBe('ok');
    if (outcome.status === 'ok') expect(outcome.extraction).toEqual(jasmine.objectContaining({ id: 'x1', text: 'Warm and plain.\n', isReading: false }));
  });

  it('decodes a waiting version and a scan, which carry no text', async () => {
    let result = service.get('w', DOC, 1);
    http.expectOne(URL).flush({ versionNumber: 1, state: 'NotExtracted', id: null, ordinal: 0, origin: null, text: null, reason: null, at: null, isReading: true });
    let outcome = await result;
    expect(outcome.status === 'ok' && outcome.extraction.isReading && outcome.extraction.state === 'NotExtracted').toBeTrue();

    result = service.get('w', DOC, 1);
    http.expectOne(URL).flush({ versionNumber: 1, state: 'Unsupported', id: 'x2', ordinal: 1, origin: 'Extracted', text: null, reason: 'No text layer.', at: 'now' });
    outcome = await result;
    expect(outcome.status === 'ok' && outcome.extraction.reason === 'No text layer.').toBeTrue();
  });

  it('refuses a response that breaks the text-exactly-when-it-succeeded rule', async () => {
    const result = service.get('w', DOC, 1);
    http.expectOne(URL).flush({ ...SUCCEEDED, text: null });
    expect(await result).toEqual({ status: 'unavailable' });
  });

  it('answers not_found for an unknown document and unavailable for anything else', async () => {
    let result = service.get('w', DOC, 1);
    http.expectOne(URL).flush({}, { status: 404, statusText: 'x' });
    expect((await result).status).toBe('not_found');
    result = service.get('w', DOC, 1);
    http.expectOne(URL).flush({}, { status: 503, statusText: 'x' });
    expect((await result).status).toBe('unavailable');
  });

  it('sends a correction as JSON with the key and no workspace field', async () => {
    const result = service.correct('w', DOC, 1, { expectedExtractionId: 'x1', text: 'Fixed.\n', reason: 'Corrected by me' }, 'key-1');
    const req = http.expectOne(`${URL}/corrections`);
    expect(req.request.headers.get('Idempotency-Key')).toBe('key-1');
    expect(req.request.body).toEqual({ expectedExtractionId: 'x1', text: 'Fixed.\n', reason: 'Corrected by me' });
    expect(JSON.stringify(req.request.body)).not.toMatch(/workspace/i);
    req.flush({ ...SUCCEEDED, id: 'x2', ordinal: 2, origin: 'Corrected' }, { status: 201, statusText: 'Created' });
    const outcome = await result;
    expect(outcome.status === 'ok' && outcome.extraction.origin === 'Corrected').toBeTrue();
  });

  it('asks for a read again, quoting the artifact being looked at (or null)', async () => {
    let result = service.retry('w', DOC, 1, 'x1', 'key-2');
    let req = http.expectOne(`${URL}/retry`);
    expect(req.request.headers.get('Idempotency-Key')).toBe('key-2');
    expect(req.request.body).toEqual({ expectedExtractionId: 'x1' });
    req.flush({ ...SUCCEEDED, state: 'Failed', text: null, isReading: true }, { status: 202, statusText: 'Accepted' });
    const outcome = await result;
    expect(outcome.status === 'ok' && outcome.extraction.isReading).toBeTrue();

    result = service.retry('w', DOC, 1, null, 'key-3');
    req = http.expectOne(`${URL}/retry`);
    expect(req.request.body).toEqual({ expectedExtractionId: null });
    req.flush({}, { status: 503, statusText: 'x' });
    expect(await result).toEqual({ status: 'failed', reason: 'unavailable' });
  });

  const cases: readonly [number, object, string][] = [
    [409, { code: 'brand.source.extraction.conflict' }, 'conflict'],
    [409, { code: 'brand.source.extraction.not_retryable.conflict' }, 'not_retryable'],
    [409, { code: 'brand.source.archived.conflict' }, 'archived'],
    [409, { code: 'brand.source.extraction.superseded.conflict' }, 'superseded'],
    [409, { code: 'brand.source.extraction.pending.conflict' }, 'pending'],
    [409, {}, 'conflict'],
    [400, { code: 'brand.source.invalid_request' }, 'invalid'],
    [400, {}, 'invalid'],
    [403, { code: 'brand.source.forbidden' }, 'forbidden'],
    [404, { code: 'brand.source.not_found' }, 'not_found'],
    [422, { code: 'idempotency.key_reused' }, 'key_reused'],
    [503, { code: 'brand.source.storage.unavailable' }, 'unavailable'],
    [500, {}, 'unavailable'],
  ];

  for (const [status, body, reason] of cases) {
    it(`maps ${status} ${JSON.stringify(body)} to ${reason}`, async () => {
      const result = service.correct('w', DOC, 1, { expectedExtractionId: 'x1', text: 't\n', reason: 'r' }, 'k');
      http.expectOne(`${URL}/corrections`).flush(body, { status, statusText: 'x' });
      expect(await result).toEqual({ status: 'failed', reason: reason as never });
    });
  }
});
