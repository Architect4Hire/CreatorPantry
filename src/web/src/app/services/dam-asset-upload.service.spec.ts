import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { lastValueFrom, toArray } from 'rxjs';

import { RuntimeConfigService } from '../core/runtime-config.service';
import { DamAssetService, DamAssetUploadEvent } from './dam-asset.service';

const GATEWAY = 'https://gateway.example';
const ROUTE = `${GATEWAY}/api/v1/workspaces/cozy-fall/dam-assets`;
const ASSET = '7a1d3e5f-2222-4333-8444-555566667777';

function assetBody(): Record<string, unknown> {
  return {
    id: ASSET,
    title: 'Plum tart',
    kind: 'Original',
    currentVersionNumber: 1,
    mediaType: 'image/png',
    width: 800,
    height: 600,
    sizeBytes: 1234,
    sourceGeneratedImageId: null,
    promptRecordId: null,
    recipeId: null,
    createdAt: '2026-10-10T12:00:00+00:00',
  };
}

describe('DamAssetService — uploading a picture', () => {
  let service: DamAssetService;
  let http: HttpTestingController;

  const file = new File(['x'], 'IMG_0042.png', { type: 'image/png' });

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

  function upload(altText = ''): Promise<DamAssetUploadEvent[]> {
    return lastValueFrom(service.upload('cozy-fall', file, { title: 'Plum tart', altText }, 'key-1').pipe(toArray()));
  }

  async function failedWith(status: number, body: Record<string, unknown> | null = null): Promise<DamAssetUploadEvent> {
    const events = upload();
    http.expectOne(ROUTE).flush(body, { status, statusText: 'Refused' });

    return (await events)[0];
  }

  /** The workspace is named by the route alone, and the form carries nothing that says who owns the asset. */
  it('posts the file and the creator’s details as a form, with credentials and the key', async () => {
    const events = upload('A tart on a blue plate.');
    const request = http.expectOne(ROUTE);

    expect(request.request.method).toBe('POST');
    expect(request.request.withCredentials).toBeTrue();
    expect(request.request.headers.get('Idempotency-Key')).toBe('key-1');

    const form = request.request.body as FormData;
    expect(Array.from(form.keys()).sort()).toEqual(['altText', 'file', 'title']);
    expect(form.get('title')).toBe('Plum tart');
    expect(form.get('altText')).toBe('A tart on a blue plate.');
    expect((form.get('file') as File).name).toBe('IMG_0042.png');

    request.flush(assetBody(), { status: 201, statusText: 'Created' });

    const done = (await events).at(-1);
    expect(done).toEqual(jasmine.objectContaining({ kind: 'done' }));
    if (done?.kind === 'done' && done.outcome.status === 'created') {
      expect(done.outcome.asset.id).toBe(ASSET);
      expect(done.outcome.asset.kind).toBe('Original');
    } else {
      fail('expected a created asset');
    }
  });

  /** Nothing has looked at the pixels, so alt text is sent only when the creator wrote some. */
  it('sends no alt text when the creator gave none', async () => {
    const events = upload();
    const request = http.expectOne(ROUTE);

    expect((request.request.body as FormData).has('altText')).toBeFalse();

    request.flush(assetBody(), { status: 201, statusText: 'Created' });
    await events;
  });

  it('gives each refusal its own reason', async () => {
    const reasonOf = (event: DamAssetUploadEvent) =>
      event.kind === 'done' && event.outcome.status === 'failed' ? event.outcome.reason : null;

    expect(reasonOf(await failedWith(413))).toBe('too_large');
    expect(reasonOf(await failedWith(422))).toBe('unsupported');
    expect(reasonOf(await failedWith(403))).toBe('forbidden');
    expect(reasonOf(await failedWith(503))).toBe('unavailable');
  });

  it('carries the server’s field errors for a refused request', async () => {
    const event = await failedWith(400, { code: 'media.asset.invalid_request', errors: { title: ['A title is required.'] } });

    expect(event).toEqual({
      kind: 'done',
      outcome: { status: 'failed', reason: 'invalid', fieldErrors: { title: ['A title is required.'] } },
    });
  });

  it('reports a response it cannot read as unavailable rather than as a created asset', async () => {
    const events = upload();
    http.expectOne(ROUTE).flush({ id: ASSET }, { status: 201, statusText: 'Created' });

    const done = (await events).at(-1);
    expect(done).toEqual({ kind: 'done', outcome: { status: 'failed', reason: 'unavailable', fieldErrors: {} } });
  });
});
