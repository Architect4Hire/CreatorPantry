import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';

import { RuntimeConfigService } from '../core/runtime-config.service';
import { AiProposalService } from './ai-proposal.service';

const RECIPE_ID = '2a1b7c6d-0000-4000-8000-000000000001';
const REQUEST_ID = '9f000000-0000-4000-8000-000000000009';
const BASE = `https://gateway.example/api/v1/workspaces/cozy-fall/recipes/${RECIPE_ID}/ai-proposals`;
const STATUS_URL = `${BASE}/${REQUEST_ID}`;
const DISPOSITION_URL = `${STATUS_URL}/disposition`;

function statusPayload(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    aiProposalRequestId: REQUEST_ID,
    status: 'Requested',
    taskType: 'Diagnostic',
    scope: 'WholeRecipe',
    sourceVersionId: 'v3',
    requestedAt: '2026-03-12T14:02:00Z',
    statusChangedAt: '2026-03-12T14:02:00Z',
    failureCategory: null,
    proposal: null,
    ...overrides,
  };
}

function problem(code: string, title = 'Refused'): Record<string, unknown> {
  return { code, title, traceId: 'trace-1' };
}

describe('AiProposalService', () => {
  let service: AiProposalService;
  let http: HttpTestingController;

  beforeEach(async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);

    const runtimeConfig = TestBed.inject(RuntimeConfigService);
    const loading = runtimeConfig.load();
    http.expectOne('/runtime-config.json').flush({ gatewayUrl: 'https://gateway.example' });
    await loading;

    service = TestBed.inject(AiProposalService);
  });

  afterEach(() => http.verify());

  describe('requestProposal', () => {
    it('posts the allow-listed request with the callers idempotency key', async () => {
      const pending = service.requestProposal(
        'cozy-fall',
        RECIPE_ID,
        { task: 'diagnostic', scope: 'WholeRecipe', sourceVersionId: 'v3' },
        'key-1',
      );

      const request = http.expectOne(BASE);
      expect(request.request.method).toBe('POST');
      expect(request.request.headers.get('Idempotency-Key')).toBe('key-1');
      expect(request.request.body).toEqual({ task: 'diagnostic', scope: 'WholeRecipe', sourceVersionId: 'v3' });

      request.flush(statusPayload(), { status: 202, statusText: 'Accepted' });

      const outcome = await pending;
      expect(outcome.status).toBe('accepted');
      if (outcome.status === 'accepted') {
        expect(outcome.operation.aiProposalRequestId).toBe(REQUEST_ID);
        expect(outcome.replayed).toBeFalse();
      }
    });

    it('reports a replay so a caller does not think it bought a second answer', async () => {
      const pending = service.requestProposal(
        'cozy-fall',
        RECIPE_ID,
        { task: 'diagnostic', scope: 'WholeRecipe', sourceVersionId: 'v3' },
        'key-1',
      );

      http
        .expectOne(BASE)
        .flush(statusPayload(), { status: 202, statusText: 'Accepted', headers: { 'Idempotent-Replayed': 'true' } });

      const outcome = await pending;
      expect(outcome.status === 'accepted' && outcome.replayed).toBeTrue();
    });

    // Four 400s with four different remedies, told apart by the stable code and nothing else.
    const four: { code: string; expected: string }[] = [
      { code: 'ai.task.unknown', expected: 'task_unknown' },
      { code: 'ai.task.not_enabled', expected: 'task_not_enabled' },
      { code: 'ai.sourceVersion.invalid_request', expected: 'source_version_invalid' },
      { code: 'ai.something.else', expected: 'validation_failed' },
    ];

    for (const { code, expected } of four) {
      it(`maps ${code} to ${expected}`, async () => {
        const pending = service.requestProposal(
          'cozy-fall',
          RECIPE_ID,
          { task: 'diagnostic', scope: 'WholeRecipe', sourceVersionId: 'v3' },
          'key-1',
        );

        http.expectOne(BASE).flush(problem(code), { status: 400, statusText: 'Bad Request' });

        expect((await pending).status).toBe(expected);
      });
    }

    it('reads a reused key as its own outcome, not as a field problem', async () => {
      const pending = service.requestProposal(
        'cozy-fall',
        RECIPE_ID,
        { task: 'diagnostic', scope: 'WholeRecipe', sourceVersionId: 'v3' },
        'key-1',
      );

      http
        .expectOne(BASE)
        .flush(problem('idempotency.key_reused'), { status: 422, statusText: 'Unprocessable Content' });

      expect((await pending).status).toBe('idempotency_key_conflict');
    });

    it('maps the remaining statuses', async () => {
      const cases: { status: number; expected: string }[] = [
        { status: 403, expected: 'forbidden' },
        { status: 404, expected: 'not_found' },
        { status: 409, expected: 'conflict' },
        { status: 500, expected: 'unavailable' },
      ];

      for (const { status, expected } of cases) {
        const pending = service.requestProposal(
          'cozy-fall',
          RECIPE_ID,
          { task: 'diagnostic', scope: 'WholeRecipe', sourceVersionId: 'v3' },
          'key-1',
        );
        http.expectOne(BASE).flush(problem('ai.whatever'), { status, statusText: 'x' });
        expect((await pending).status).toBe(expected);
      }
    });

    it('answers unavailable without a request when the gateway is not configured yet', async () => {
      TestBed.resetTestingModule();
      TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
      const isolated = TestBed.inject(AiProposalService);

      const outcome = await isolated.requestProposal(
        'cozy-fall',
        RECIPE_ID,
        { task: 'diagnostic', scope: 'WholeRecipe', sourceVersionId: 'v3' },
        'key-1',
      );

      expect(outcome.status).toBe('unavailable');
      TestBed.inject(HttpTestingController).verify();
    });
  });

  describe('watchStatus', () => {
    it('reads the operation, and a status before any proposal exists is a normal answer', async () => {
      const outcome = firstValueFrom(service.watchStatus('cozy-fall', RECIPE_ID, REQUEST_ID));

      http.expectOne(STATUS_URL).flush(statusPayload({ status: 'Running' }));

      const result = await outcome;
      expect(result.status).toBe('found');
      if (result.status === 'found') expect(result.operation.status).toBe('Running');
    });

    it('never errors: a failed read is a value', async () => {
      const outcome = firstValueFrom(service.watchStatus('cozy-fall', RECIPE_ID, REQUEST_ID));

      http.expectOne(STATUS_URL).flush('', { status: 503, statusText: 'Service Unavailable' });

      expect((await outcome).status).toBe('unavailable');
    });

    it('distinguishes a request that is not there from one it may not read', async () => {
      const gone = firstValueFrom(service.watchStatus('cozy-fall', RECIPE_ID, REQUEST_ID));
      http.expectOne(STATUS_URL).flush(problem('ai.request.not_found'), { status: 404, statusText: 'Not Found' });
      expect((await gone).status).toBe('not_found');

      const refused = firstValueFrom(service.watchStatus('cozy-fall', RECIPE_ID, REQUEST_ID));
      http.expectOne(STATUS_URL).flush(problem('x.forbidden'), { status: 403, statusText: 'Forbidden' });
      expect((await refused).status).toBe('forbidden');
    });

    // Unsubscribing aborts the request, which is what makes a poll cancellable.
    it('cancels the request when the subscriber goes away', () => {
      const subscription = service.watchStatus('cozy-fall', RECIPE_ID, REQUEST_ID).subscribe();
      const request = http.expectOne(STATUS_URL);

      subscription.unsubscribe();

      expect(request.cancelled).toBeTrue();
    });

    it('reports a payload it cannot decode as unavailable rather than half-reading it', async () => {
      const outcome = firstValueFrom(service.watchStatus('cozy-fall', RECIPE_ID, REQUEST_ID));

      http.expectOne(STATUS_URL).flush(statusPayload({ status: 'Ruminating' }));

      expect((await outcome).status).toBe('unavailable');
    });
  });

  describe('disposition', () => {
    it('posts the decision and the confirmed ids, with no idempotency key', async () => {
      const pending = service.disposition('cozy-fall', RECIPE_ID, REQUEST_ID, {
        decision: 'AcceptSelected',
        acceptedChangeIds: ['c1'],
        wasHelpful: true,
        comment: 'useful',
      });

      const request = http.expectOne(DISPOSITION_URL);
      expect(request.request.method).toBe('POST');
      expect(request.request.headers.has('Idempotency-Key')).toBeFalse();
      expect(request.request.body).toEqual({
        decision: 'AcceptSelected',
        acceptedChangeIds: ['c1'],
        wasHelpful: true,
        comment: 'useful',
      });

      request.flush({
        aiProposalRequestId: REQUEST_ID,
        status: 'PartiallyAccepted',
        acceptedChangeCount: 1,
        rejectedChangeCount: 2,
        recipeVersionNumber: 5,
        decidedAt: '2026-03-12T14:10:00Z',
      });

      const outcome = await pending;
      expect(outcome.status).toBe('decided');
      if (outcome.status === 'decided') expect(outcome.result.recipeVersionNumber).toBe(5);
    });

    it('carries the servers own sentence when a selection is refused', async () => {
      const pending = service.disposition('cozy-fall', RECIPE_ID, REQUEST_ID, {
        decision: 'AcceptAll',
        acceptedChangeIds: ['c1'],
        wasHelpful: null,
        comment: null,
      });

      http
        .expectOne(DISPOSITION_URL)
        .flush(problem('ai.selection.invalid_request', 'Accepting everything must name every change.'), {
          status: 400,
          statusText: 'Bad Request',
        });

      const outcome = await pending;
      expect(outcome.status).toBe('selection_invalid');
      if (outcome.status === 'selection_invalid') {
        expect(outcome.message).toBe('Accepting everything must name every change.');
      }
    });

    // Three 409s whose remedies are opposites: start again, unarchive, or stop asking.
    const conflicts: { code: string; expected: string }[] = [
      { code: 'recipes.recipe.conflict', expected: 'source_stale' },
      { code: 'recipes.archived.conflict', expected: 'recipe_archived' },
      { code: 'ai.proposal.conflict', expected: 'already_decided' },
    ];

    for (const { code, expected } of conflicts) {
      it(`maps the ${code} conflict to ${expected}`, async () => {
        const pending = service.disposition('cozy-fall', RECIPE_ID, REQUEST_ID, {
          decision: 'Reject',
          acceptedChangeIds: [],
          wasHelpful: null,
          comment: null,
        });

        http.expectOne(DISPOSITION_URL).flush(problem(code), { status: 409, statusText: 'Conflict' });

        expect((await pending).status).toBe(expected);
      });
    }

    it('treats an unlabelled conflict as already decided, which is the safe reading', async () => {
      const pending = service.disposition('cozy-fall', RECIPE_ID, REQUEST_ID, {
        decision: 'Reject',
        acceptedChangeIds: [],
        wasHelpful: null,
        comment: null,
      });

      http.expectOne(DISPOSITION_URL).flush('', { status: 409, statusText: 'Conflict' });

      expect((await pending).status).toBe('already_decided');
    });

    it('maps role, missing and transport failures', async () => {
      const cases: { status: number; expected: string }[] = [
        { status: 403, expected: 'forbidden' },
        { status: 404, expected: 'not_found' },
        { status: 502, expected: 'unavailable' },
      ];

      for (const { status, expected } of cases) {
        const pending = service.disposition('cozy-fall', RECIPE_ID, REQUEST_ID, {
          decision: 'Reject',
          acceptedChangeIds: [],
          wasHelpful: null,
          comment: null,
        });
        http.expectOne(DISPOSITION_URL).flush(problem('x'), { status, statusText: 'x' });
        expect((await pending).status).toBe(expected);
      }
    });

    it('reports field errors when the body itself was refused', async () => {
      const pending = service.disposition('cozy-fall', RECIPE_ID, REQUEST_ID, {
        decision: 'AcceptSelected',
        acceptedChangeIds: ['c1'],
        wasHelpful: null,
        comment: null,
      });

      http.expectOne(DISPOSITION_URL).flush(
        { ...problem('ai.disposition.invalid_request'), errors: { comment: ['Keep the comment shorter.'] } },
        { status: 400, statusText: 'Bad Request' },
      );

      const outcome = await pending;
      expect(outcome.status).toBe('validation_failed');
      if (outcome.status === 'validation_failed') {
        expect(outcome.fieldErrors['comment']).toEqual(['Keep the comment shorter.']);
      }
    });
  });
});
