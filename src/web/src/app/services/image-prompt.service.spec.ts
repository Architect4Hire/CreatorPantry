import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';

import { RuntimeConfigService } from '../core/runtime-config.service';
import { ImagePromptService } from './image-prompt.service';

const BASE = 'https://gateway.example/api/v1/workspaces/cozy-fall/image-prompt-requests';

const REQUEST = {
  conceptRequestId: 'r-concept',
  conceptId: 'k-1',
  shotKind: 'Hero' as const,
  channelKey: null,
  briefDocumentId: 'd-brief',
  sceneOverrides: [],
  styleOverrides: [],
};

function accepted(): Record<string, unknown> {
  return {
    aiProposalRequestId: 'r-1',
    status: 'Requested',
    taskType: 'ImagePrompt',
    scope: 'Unspecified',
    sourceVersionId: null,
    requestedAt: '2026-10-07T12:00:00Z',
    statusChangedAt: '2026-10-07T12:00:00Z',
    failureCategory: null,
    proposal: null,
  };
}

describe('ImagePromptService', () => {
  let service: ImagePromptService;
  let http: HttpTestingController;

  beforeEach(async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);

    const runtimeConfig = TestBed.inject(RuntimeConfigService);
    const loading = runtimeConfig.load();
    http.expectOne('/runtime-config.json').flush({ gatewayUrl: 'https://gateway.example' });
    await loading;

    service = TestBed.inject(ImagePromptService);
  });

  afterEach(() => http.verify());

  it('posts the concept, its shot and the brief, leaving out what was not given', async () => {
    const pending = service.request('cozy-fall', REQUEST, 'key-1');

    const request = http.expectOne(BASE);
    expect(request.request.body).toEqual({
      conceptRequestId: 'r-concept',
      conceptId: 'k-1',
      shotKind: 'Hero',
      briefDocumentId: 'd-brief',
    });
    request.flush(accepted(), { status: 202, statusText: 'Accepted' });

    expect((await pending).status).toBe('accepted');
  });

  it('reads a concept that cannot be composed from as a refusal, in the server’s own words', async () => {
    const pending = service.request('cozy-fall', REQUEST, 'key-1');

    http.expectOne(BASE).flush(
      { code: 'ai.imagePromptConcept.not_found', title: 'That look is no longer there.' },
      { status: 404, statusText: 'Not Found' },
    );

    const outcome = await pending;
    expect(outcome.status).toBe('refused');
    if (outcome.status === 'refused') {
      expect(outcome.code).toBe('ai.imagePromptConcept.not_found');
      expect(outcome.message).toBe('That look is no longer there.');
    }
  });

  it('reads a brief that is not this workspace’s as a refusal rather than a reused key', async () => {
    const pending = service.request('cozy-fall', REQUEST, 'key-1');

    http
      .expectOne(BASE)
      .flush({ code: 'ai.imagePromptBrief.not_found', title: 'No such brief.' }, { status: 422, statusText: 'x' });

    expect((await pending).status).toBe('refused');
  });

  it('reads where a request has got to, by its id', async () => {
    const pending = firstValueFrom(service.watch('cozy-fall', 'r-1'));
    http.expectOne(`${BASE}/r-1`).flush(accepted());

    expect((await pending).status).toBe('found');
  });
});
