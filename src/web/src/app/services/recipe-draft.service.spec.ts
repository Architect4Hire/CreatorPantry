import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';

import { RuntimeConfigService } from '../core/runtime-config.service';
import { RecipeDraftService } from './recipe-draft.service';

const REQUEST_ID = '9f000000-0000-4000-8000-00000000000d';
const BASE = 'https://gateway.example/api/v1/workspaces/cozy-fall/recipe-draft-requests';
const STATUS_URL = `${BASE}/${REQUEST_ID}`;

function statusPayload(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    aiProposalRequestId: REQUEST_ID,
    status: 'Requested',
    taskType: 'RecipeFirstDraft',
    scope: 'NotApplicable',
    sourceVersionId: null,
    requestedAt: '2026-09-28T14:02:00Z',
    statusChangedAt: '2026-09-28T14:02:00Z',
    failureCategory: null,
    proposal: null,
    ...overrides,
  };
}

describe('RecipeDraftService', () => {
  let service: RecipeDraftService;
  let http: HttpTestingController;

  beforeEach(async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);

    const runtimeConfig = TestBed.inject(RuntimeConfigService);
    const loading = runtimeConfig.load();
    http.expectOne('/runtime-config.json').flush({ gatewayUrl: 'https://gateway.example' });
    await loading;

    service = TestBed.inject(RecipeDraftService);
  });

  afterEach(() => http.verify());

  it('reads a request from the workspace-scoped route, with credentials', async () => {
    const pending = firstValueFrom(service.watchStatus('cozy-fall', REQUEST_ID));

    const request = http.expectOne(STATUS_URL);
    expect(request.request.method).toBe('GET');
    expect(request.request.withCredentials).toBe(true);
    request.flush(statusPayload());

    const outcome = await pending;
    expect(outcome.status).toBe('found');
  });

  /** Polling before the draft exists is a 200 carrying a status, not a 404. */
  it('reads a queued request as found rather than missing', async () => {
    const pending = firstValueFrom(service.watchStatus('cozy-fall', REQUEST_ID));
    http.expectOne(STATUS_URL).flush(statusPayload({ status: 'Running' }));

    const outcome = await pending;
    expect(outcome.status).toBe('found');
    if (outcome.status === 'found') expect(outcome.operation.status).toBe('Running');
  });

  it('carries the proposal once there is one', async () => {
    const pending = firstValueFrom(service.watchStatus('cozy-fall', REQUEST_ID));
    http.expectOne(STATUS_URL).flush(
      statusPayload({
        status: 'Proposed',
        proposal: {
          proposalId: 'p1',
          outputSchemaVersion: 'recipe.first-draft.v1',
          promptTemplateId: 'recipe.first-draft',
          promptTemplateVersion: '1.0.0',
          promptTemplateBodyChecksum: 'sha256:abc',
          providerName: 'test-provider',
          modelName: 'test-model',
          createdAt: '2026-09-28T14:03:00Z',
          changes: [],
          warnings: [{ kind: 'UnresolvedQuestion', message: 'What chili oil brand?', changeId: null }],
        },
      }),
    );

    const outcome = await pending;
    expect(outcome.status).toBe('found');
    if (outcome.status === 'found') {
      expect(outcome.operation.proposal!.warnings[0].kind).toBe('UnresolvedQuestion');
    }
  });

  it('reports an unknown request as not found', async () => {
    const pending = firstValueFrom(service.watchStatus('cozy-fall', REQUEST_ID));
    http.expectOne(STATUS_URL).flush({}, { status: 404, statusText: 'Not Found' });

    expect((await pending).status).toBe('not_found');
  });

  it('reports a refused read as forbidden', async () => {
    const pending = firstValueFrom(service.watchStatus('cozy-fall', REQUEST_ID));
    http.expectOne(STATUS_URL).flush({}, { status: 403, statusText: 'Forbidden' });

    expect((await pending).status).toBe('forbidden');
  });

  /** Never errors the Observable: one failed poll must not kill a polling loop built on this. */
  it('reports a server failure as a value rather than an error', async () => {
    const pending = firstValueFrom(service.watchStatus('cozy-fall', REQUEST_ID));
    http.expectOne(STATUS_URL).flush({}, { status: 500, statusText: 'Server Error' });

    expect((await pending).status).toBe('unavailable');
  });

  it('reports an undecodable body as unavailable rather than pretending it decoded', async () => {
    const pending = firstValueFrom(service.watchStatus('cozy-fall', REQUEST_ID));
    http.expectOne(STATUS_URL).flush({ nonsense: true });

    expect((await pending).status).toBe('unavailable');
  });

  it('escapes the request id into the path', async () => {
    const pending = firstValueFrom(service.watchStatus('cozy fall', 'a b'));

    const request = http.expectOne(
      'https://gateway.example/api/v1/workspaces/cozy%20fall/recipe-draft-requests/a%20b',
    );
    request.flush(statusPayload());

    await pending;
  });

  /**
   * The absences are the contract. A workspace-level request has no disposition route, and nothing in the app
   * asks for a first draft yet — a typed method for either would be a promise this client cannot keep.
   */
  it('offers reading and nothing else', () => {
    const methods = Object.getOwnPropertyNames(RecipeDraftService.prototype).filter(
      (name) => name !== 'constructor',
    );

    expect(methods).toEqual(['watchStatus']);
  });
});
