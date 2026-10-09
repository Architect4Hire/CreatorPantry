import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';

import { RuntimeConfigService } from '../core/runtime-config.service';
import { ReferenceImageService } from './reference-image.service';

const BASE = 'https://gateway.example/api/v1/workspaces/cozy-fall/reference-image-requests';

function accepted(): Record<string, unknown> {
  return {
    aiProposalRequestId: 'r-1',
    status: 'Requested',
    taskType: 'ReferenceImageAnalysis',
    scope: 'Unspecified',
    sourceVersionId: null,
    requestedAt: '2026-10-07T12:00:00Z',
    statusChangedAt: '2026-10-07T12:00:00Z',
    failureCategory: null,
    proposal: null,
  };
}

describe('ReferenceImageService', () => {
  let service: ReferenceImageService;
  let http: HttpTestingController;

  beforeEach(async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);

    const runtimeConfig = TestBed.inject(RuntimeConfigService);
    const loading = runtimeConfig.load();
    http.expectOne('/runtime-config.json').flush({ gatewayUrl: 'https://gateway.example' });
    await loading;

    service = TestBed.inject(ReferenceImageService);
  });

  afterEach(() => http.verify());

  it('names the image and sends no bytes, because the upload route already inspected them', async () => {
    const pending = service.request('cozy-fall', { picture: { source: 'BrandDocument', referenceDocumentId: 'd-1' }, note: 'the light' }, 'key-1');

    const request = http.expectOne(BASE);
    expect(request.request.body).toEqual({ referenceDocumentId: 'd-1', note: 'the light' });
    expect(request.request.body instanceof FormData).toBeFalse();
    request.flush(accepted(), { status: 202, statusText: 'Accepted' });

    expect((await pending).status).toBe('accepted');
  });

  it('leaves the note out when there is none', async () => {
    const pending = service.request('cozy-fall', { picture: { source: 'BrandDocument', referenceDocumentId: 'd-1' }, note: '   ' }, 'key-1');

    const request = http.expectOne(BASE);
    expect(request.request.body).toEqual({ referenceDocumentId: 'd-1' });

    request.flush(accepted(), { status: 202, statusText: 'Accepted' });
    await pending;
  });

  it('answers one refusal for a document that is missing, a neighbour’s, or not an image', async () => {
    const pending = service.request('cozy-fall', { picture: { source: 'BrandDocument', referenceDocumentId: 'd-1' }, note: '' }, 'key-1');

    http
      .expectOne(BASE)
      .flush({ code: 'ai.referenceImage.not_found', title: 'That cannot be read.' }, { status: 404, statusText: 'x' });

    const outcome = await pending;
    expect(outcome.status).toBe('refused');
    if (outcome.status === 'refused') expect(outcome.message).toBe('That cannot be read.');
  });

  it('reads where a request has got to, by its id', async () => {
    const pending = firstValueFrom(service.watch('cozy-fall', 'r-1'));
    http.expectOne(`${BASE}/r-1`).flush(accepted());

    expect((await pending).status).toBe('found');
  });
});
