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
   * The absence is the contract. There is no reject method: discarding a draft is still client-side, and a
   * typed call for it before a screen sends one would be a promise this client does not keep.
   */
  it('offers asking, accepting and reading, and no way to reject', () => {
    const methods = Object.getOwnPropertyNames(RecipeDraftService.prototype).filter(
      (name) => name !== 'constructor',
    );

    expect(methods).toEqual(['requestDraft', 'acceptDraft', 'watchStatus']);
  });

  describe('acceptDraft', () => {
    const URL = `${STATUS_URL}/acceptance`;
    const RECIPE_ID = '7c000000-0000-4000-8000-0000000000aa';

    const request = {
      acceptedChangeIds: ['c1', 'c2', 'c3'],
      rewrites: [{ changeId: 'c2', field: 'displayText', value: '3 tbsp doubanjiang' }],
    };

    function reply(overrides: Record<string, unknown> = {}): Record<string, unknown> {
      return {
        aiProposalRequestId: REQUEST_ID,
        status: 'Accepted',
        recipeId: RECIPE_ID,
        recipeVersionNumber: 1,
        acceptedChangeCount: 3,
        rejectedChangeCount: 0,
        rewrittenChangeCount: 1,
        droppedChangeCount: 0,
        replayed: false,
        decidedAt: '2026-10-09T12:00:00+00:00',
        ...overrides,
      };
    }

    it('posts the whole draft, named part by part, with the rewrites — and no idempotency key', async () => {
      const pending = service.acceptDraft('cozy-fall', REQUEST_ID, request);

      const sent = http.expectOne(URL);
      expect(sent.request.method).toBe('POST');
      expect(sent.request.withCredentials).toBeTrue();

      // The route's own rule makes a retry safe. A key minted here would be a second account of it.
      expect(sent.request.headers.has('Idempotency-Key')).toBeFalse();
      expect(sent.request.body).toEqual({
        decision: 'AcceptAll',
        acceptedChangeIds: ['c1', 'c2', 'c3'],
        edits: [{ changeId: 'c2', field: 'displayText', value: '3 tbsp doubanjiang' }],
      });
      sent.flush(reply());

      expect(await pending).toEqual({ status: 'accepted', recipeId: RECIPE_ID, replayed: false });
    });

    it('names no workspace and no recipe in the body', async () => {
      const pending = service.acceptDraft('cozy-fall', REQUEST_ID, request);

      const sent = http.expectOne(URL);
      const body = JSON.stringify(sent.request.body);
      expect(body).not.toContain('workspace');
      expect(body).not.toContain('recipeId');
      sent.flush(reply());
      await pending;
    });

    it('reports a replayed acceptance as the same recipe, and says it was a replay', async () => {
      const pending = service.acceptDraft('cozy-fall', REQUEST_ID, request);

      http.expectOne(URL).flush(reply({ replayed: true }));

      expect(await pending).toEqual({ status: 'accepted', recipeId: RECIPE_ID, replayed: true });
    });

    it('treats a decision with no recipe behind it as already decided, not as a recipe', async () => {
      const pending = service.acceptDraft('cozy-fall', REQUEST_ID, request);

      http.expectOne(URL).flush(reply({ status: 'Rejected', recipeId: null, replayed: true }));

      expect((await pending).status).toBe('already_decided');
    });

    it('treats a body it cannot read as unavailable, never as an acceptance', async () => {
      const pending = service.acceptDraft('cozy-fall', REQUEST_ID, request);

      http.expectOne(URL).flush('nonsense');

      expect((await pending).status).toBe('unavailable');
    });

    it('keeps the server’s own sentence when it will not build a recipe from what was sent', async () => {
      const pending = service.acceptDraft('cozy-fall', REQUEST_ID, request);

      http.expectOne(URL).flush(
        { code: 'ai.recipeDraftSelection.invalid_request', title: 'A recipe needs a title.' },
        { status: 400, statusText: 'Bad Request' },
      );

      expect(await pending).toEqual({ status: 'refused', message: 'A recipe needs a title.' });
    });

    const failures = [
      [400, 'ai.recipeDraftAcceptance.invalid_request', 'refused'],
      [409, 'ai.recipeDraft.conflict', 'already_decided'],
      [404, 'ai.recipeDraft.not_found', 'not_found'],
      [403, 'ai.recipeDraftAcceptance.forbidden', 'forbidden'],
      [500, 'internal', 'unavailable'],
      [0, 'network', 'unavailable'],
    ] as const;

    for (const [status, code, expected] of failures) {
      it(`answers '${expected}' to a ${status} ${code}`, async () => {
        const pending = service.acceptDraft('cozy-fall', REQUEST_ID, request);

        const sent = http.expectOne(URL);
        if (status === 0) sent.error(new ProgressEvent('error'));
        else sent.flush({ code, title: 'No.' }, { status, statusText: 'Refused' });

        expect((await pending).status).toBe(expected);
      });
    }
  });

  describe('requestDraft', () => {
    const CONCEPT_REQUEST = '9f000000-0000-4000-8000-0000000000c1';
    const CONCEPT = '9f000000-0000-4000-8000-0000000000c2';

    const request = {
      sourceConceptRequestId: CONCEPT_REQUEST,
      sourceConceptId: CONCEPT,
      brief: null,
    };

    function problem(code: string, extra: Record<string, unknown> = {}): Record<string, unknown> {
      return { code, title: 'The server’s own sentence.', traceId: 'trace-1', ...extra };
    }

    it('posts the chosen concept to the workspace route with credentials and the idempotency key', async () => {
      const pending = service.requestDraft('cozy-fall', request, 'key-1');

      const sent = http.expectOne(BASE);
      expect(sent.request.method).toBe('POST');
      expect(sent.request.withCredentials).toBeTrue();
      expect(sent.request.headers.get('Idempotency-Key')).toBe('key-1');
      expect(sent.request.body).toEqual({ sourceConceptRequestId: CONCEPT_REQUEST, sourceConceptId: CONCEPT });
      sent.flush(statusPayload(), { status: 202, statusText: 'Accepted' });

      const outcome = await pending;
      expect(outcome.status).toBe('accepted');
      if (outcome.status === 'accepted') {
        expect(outcome.operation.aiProposalRequestId).toBe(REQUEST_ID);
        expect(outcome.replayed).toBeFalse();
      }
    });

    it('sends the brief with the concept, leaving out what the creator left blank', async () => {
      const pending = service.requestDraft(
        'cozy-fall',
        {
          ...request,
          brief: {
            dishName: null,
            audience: 'Busy parents',
            course: null,
            cuisine: '   ',
            dietaryGoals: null,
            availableIngredients: null,
            exclusions: ' no nuts ',
            equipment: null,
            skill: null,
            season: null,
            timeBudget: null,
            creatorStyle: null,
          },
        },
        'key-1',
      );

      const sent = http.expectOne(BASE);
      expect(sent.request.body).toEqual({
        audience: 'Busy parents',
        exclusions: 'no nuts',
        sourceConceptRequestId: CONCEPT_REQUEST,
        sourceConceptId: CONCEPT,
      });
      sent.flush(statusPayload(), { status: 202, statusText: 'Accepted' });
      await pending;
    });

    it('names no workspace anywhere but the path', async () => {
      const pending = service.requestDraft('cozy-fall', request, 'key-1');

      const sent = http.expectOne(BASE);
      expect(JSON.stringify(sent.request.body)).not.toContain('workspace');
      sent.flush(statusPayload(), { status: 202, statusText: 'Accepted' });
      await pending;
    });

    it('reports a replayed request as accepted, and says it was a replay', async () => {
      const pending = service.requestDraft('cozy-fall', request, 'key-1');

      http
        .expectOne(BASE)
        .flush(statusPayload(), { status: 202, statusText: 'Accepted', headers: { 'Idempotent-Replayed': 'true' } });

      const outcome = await pending;
      expect(outcome.status).toBe('accepted');
      if (outcome.status === 'accepted') expect(outcome.replayed).toBeTrue();
    });

    it('treats a body it cannot read as unavailable', async () => {
      const pending = service.requestDraft('cozy-fall', request, 'key-1');

      http.expectOne(BASE).flush({ nonsense: true }, { status: 202, statusText: 'Accepted' });

      expect((await pending).status).toBe('unavailable');
    });

    const refusals = [
      [400, 'ai.recipeFirstDraft.not_enabled', 'task_not_enabled'],
      [400, 'ai.recipeFirstDraft.invalid_request', 'validation_failed'],
      [400, 'ai.recipeFirstDraft.too_large', 'refused'],
      [404, 'ai.recipeConcept.not_found', 'refused'],
      [422, 'idempotency.key_reused', 'idempotency_key_conflict'],
      [403, 'ai.quota.suspended', 'account_suspended'],
      [403, 'workspace.forbidden', 'forbidden'],
      [500, 'internal', 'unavailable'],
      [0, 'network', 'unavailable'],
    ] as const;

    for (const [status, code, expected] of refusals) {
      it(`answers '${expected}' to a ${status} ${code}`, async () => {
        const pending = service.requestDraft('cozy-fall', request, 'key-1');

        const sent = http.expectOne(BASE);
        if (status === 0) sent.error(new ProgressEvent('error'));
        else sent.flush(problem(code), { status, statusText: 'Refused' });

        expect((await pending).status).toBe(expected);
      });
    }

    it('keeps the code and the server’s own sentence on a refusal, so the screen can tell the two apart', async () => {
      const pending = service.requestDraft('cozy-fall', request, 'key-1');

      http.expectOne(BASE).flush(problem('ai.recipeFirstDraft.too_large'), { status: 400, statusText: 'Bad Request' });

      const outcome = await pending;
      expect(outcome).toEqual({
        status: 'refused',
        code: 'ai.recipeFirstDraft.too_large',
        message: 'The server’s own sentence.',
      });
    });

    it('tells a spent allowance from the edge’s own rate limit, which is also a 429', async () => {
      const spent = service.requestDraft('cozy-fall', request, 'key-1');
      http.expectOne(BASE).flush(
        problem('ai.quota.exhausted', {
          unit: 'Credits',
          allowance: 100,
          remaining: 2,
          required: 5,
          resetsAt: '2026-11-01T00:00:00+00:00',
        }),
        { status: 429, statusText: 'Too Many Requests' },
      );
      expect((await spent).status).toBe('quota_exhausted');

      const limited = service.requestDraft('cozy-fall', request, 'key-2');
      http.expectOne(BASE).flush(problem('rate_limited'), { status: 429, statusText: 'Too Many Requests' });
      expect((await limited).status).toBe('unavailable');
    });
  });
});
