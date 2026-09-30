import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';

import { RuntimeConfigService } from '../core/runtime-config.service';
import {
  CreateRecipeTestRunRequest,
  UpdateRecipeTestRunRequest,
  absent,
  submitted,
} from '../models/recipe-test-run.models';
import { RecipeTestRunService } from './recipe-test-run.service';

const RUN_ID = 't1';
const BASE = 'https://gateway.example/api/v1/workspaces/cozy-fall/recipes/r1/test-runs';

const CREATE: CreateRecipeTestRunRequest = { sourceVersionNumber: 3, testedAt: '2026-09-28T18:00:00Z' };

function update(overrides: Partial<UpdateRecipeTestRunRequest> = {}): UpdateRecipeTestRunRequest {
  return {
    expectedConcurrencyToken: 'token-1',
    testedAt: absent(),
    outcome: absent(),
    rating: absent(),
    environmentNotes: absent(),
    equipmentNotes: absent(),
    summaryNotes: absent(),
    actualYieldText: absent(),
    actualYieldQuantity: absent(),
    actualYieldUnitId: absent(),
    actualPrepTimeMinutes: absent(),
    actualCookTimeMinutes: absent(),
    actualRestTimeMinutes: absent(),
    actualTotalTimeMinutes: absent(),
    observations: absent(),
    issues: absent(),
    ...overrides,
  };
}

function createdPayload(): Record<string, unknown> {
  return {
    testRunId: RUN_ID,
    recipeId: 'r1',
    recipeVersionId: 'v3',
    sourceVersionNumber: 3,
    outcome: 'NotStated',
    testedAt: '2026-09-28T18:00:00Z',
    createdAt: '2026-09-28T19:00:00Z',
    concurrencyToken: 'token-1',
    observationIds: [],
    issueIds: [],
  };
}

function runPayload(): Record<string, unknown> {
  return {
    id: RUN_ID,
    recipeId: 'r1',
    recipeVersionId: 'v3',
    testedAt: '2026-09-28T18:00:00Z',
    testedByMembershipId: 'm1',
    outcome: 'Succeeded',
    rating: null,
    environmentNotes: null,
    equipmentNotes: null,
    summaryNotes: null,
    actualYieldText: null,
    actualYieldQuantity: null,
    actualYieldUnitId: null,
    actualPrepTimeMinutes: null,
    actualCookTimeMinutes: null,
    actualRestTimeMinutes: null,
    actualTotalTimeMinutes: null,
    createdByMembershipId: 'm1',
    updatedByMembershipId: 'm1',
    createdAt: '2026-09-28T19:00:00Z',
    updatedAt: '2026-09-28T19:05:00Z',
    concurrencyToken: 'token-2',
    observations: [],
    issues: [],
  };
}

function problem(code: string): Record<string, unknown> {
  return { code };
}

describe('RecipeTestRunService', () => {
  let service: RecipeTestRunService;
  let http: HttpTestingController;

  beforeEach(async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);

    const runtimeConfig = TestBed.inject(RuntimeConfigService);
    const loading = runtimeConfig.load();
    http.expectOne('/runtime-config.json').flush({ gatewayUrl: 'https://gateway.example' });
    await loading;

    service = TestBed.inject(RecipeTestRunService);
  });

  afterEach(() => http.verify());

  describe('createTestRun', () => {
    it('posts to the workspace-scoped route, with credentials and the idempotency key', async () => {
      const pending = service.createTestRun('cozy-fall', 'r1', CREATE, 'key-1');

      const request = http.expectOne(BASE);
      expect(request.request.method).toBe('POST');
      expect(request.request.withCredentials).toBeTrue();
      expect(request.request.headers.get('Idempotency-Key')).toBe('key-1');
      expect(request.request.body.sourceVersionNumber).toBe(3);
      request.flush(createdPayload());

      const outcome = await pending;
      expect(outcome.status).toBe('created');
      if (outcome.status === 'created') expect(outcome.testRun.testRunId).toBe(RUN_ID);
    });

    it('sends no idempotency header when the caller supplied no key', async () => {
      const pending = service.createTestRun('cozy-fall', 'r1', CREATE);

      const request = http.expectOne(BASE);
      expect(request.request.headers.has('Idempotency-Key')).toBeFalse();
      request.flush(createdPayload());

      await pending;
    });

    it('reports a replay so a caller can tell a repeat from a second recording', async () => {
      const pending = service.createTestRun('cozy-fall', 'r1', CREATE, 'key-1');
      http.expectOne(BASE).flush(createdPayload(), { headers: { 'Idempotent-Replayed': 'true' } });

      const outcome = await pending;
      expect(outcome.status === 'created' && outcome.replayed).toBeTrue();
    });

    it('carries a 400 through as field errors', async () => {
      const pending = service.createTestRun('cozy-fall', 'r1', CREATE, 'key-1');
      http
        .expectOne(BASE)
        .flush({ errors: { testedAt: ['Testers record the past.'] } }, { status: 400, statusText: 'Bad Request' });

      const outcome = await pending;
      expect(outcome.status).toBe('validation_failed');
      if (outcome.status === 'validation_failed') expect(outcome.fieldErrors['testedAt']).toEqual(['Testers record the past.']);
    });

    /** Both are 404s, and the stable code is the only thing that separates them. */
    it('tells a missing version apart from an unreadable recipe', async () => {
      const missingVersion = service.createTestRun('cozy-fall', 'r1', CREATE);
      http.expectOne(BASE).flush(
        { ...problem('recipes.version.not_found'), errors: { sourceVersionNumber: ['No such version.'] } },
        { status: 404, statusText: 'Not Found' },
      );
      const versionOutcome = await missingVersion;
      expect(versionOutcome.status).toBe('version_not_found');
      if (versionOutcome.status === 'version_not_found') {
        expect(versionOutcome.fieldErrors['sourceVersionNumber']).toEqual(['No such version.']);
      }

      const missingRecipe = service.createTestRun('cozy-fall', 'r1', CREATE);
      http
        .expectOne(BASE)
        .flush(problem('recipes.recipe.not_found'), { status: 404, statusText: 'Not Found' });
      expect((await missingRecipe).status).toBe('not_found');
    });

    it('reports an archived recipe as its own conflict', async () => {
      const pending = service.createTestRun('cozy-fall', 'r1', CREATE);
      http.expectOne(BASE).flush(problem('recipes.archived.conflict'), { status: 409, statusText: 'Conflict' });

      expect((await pending).status).toBe('archived_conflict');
    });

    it('reports a reused key as a key conflict rather than a validation failure', async () => {
      const pending = service.createTestRun('cozy-fall', 'r1', CREATE, 'key-1');
      http.expectOne(BASE).flush({}, { status: 422, statusText: 'Unprocessable Content' });

      expect((await pending).status).toBe('idempotency_key_conflict');
    });

    it('reports the Contributor bar as forbidden', async () => {
      const pending = service.createTestRun('cozy-fall', 'r1', CREATE);
      http.expectOne(BASE).flush({}, { status: 403, statusText: 'Forbidden' });

      expect((await pending).status).toBe('forbidden');
    });

    it('reports a body it cannot decode as unavailable rather than as a success', async () => {
      const pending = service.createTestRun('cozy-fall', 'r1', CREATE);
      http.expectOne(BASE).flush({ testRunId: RUN_ID });

      expect((await pending).status).toBe('unavailable');
    });
  });

  describe('updateTestRun', () => {
    it('patches the run and returns it with the refreshed token', async () => {
      const pending = service.updateTestRun('cozy-fall', 'r1', RUN_ID, update({ rating: submitted(5) }), 'key-2');

      const request = http.expectOne(`${BASE}/${RUN_ID}`);
      expect(request.request.method).toBe('PATCH');
      expect(request.request.withCredentials).toBeTrue();
      expect(request.request.headers.get('Idempotency-Key')).toBe('key-2');
      expect(request.request.body).toEqual({ expectedConcurrencyToken: 'token-1', rating: 5 });
      request.flush(runPayload());

      const outcome = await pending;
      expect(outcome.status).toBe('updated');
      if (outcome.status === 'updated') expect(outcome.testRun.concurrencyToken).toBe('token-2');
    });

    /**
     * Three 409s with three different remedies. Collapsing them would tell somebody to retry a request that
     * can only ever be refused, or to re-read a test that has not moved.
     */
    it('separates a stale token, a removed resolved issue, and an archived recipe', async () => {
      const stale = service.updateTestRun('cozy-fall', 'r1', RUN_ID, update());
      http.expectOne(`${BASE}/${RUN_ID}`).flush(problem('recipes.testRun.conflict'), { status: 409, statusText: 'Conflict' });
      expect((await stale).status).toBe('conflict');

      const removal = service.updateTestRun('cozy-fall', 'r1', RUN_ID, update());
      http
        .expectOne(`${BASE}/${RUN_ID}`)
        .flush(problem('recipes.testIssue.removal.conflict'), { status: 409, statusText: 'Conflict' });
      expect((await removal).status).toBe('issue_removal_conflict');

      const archived = service.updateTestRun('cozy-fall', 'r1', RUN_ID, update());
      http
        .expectOne(`${BASE}/${RUN_ID}`)
        .flush(problem('recipes.archived.conflict'), { status: 409, statusText: 'Conflict' });
      expect((await archived).status).toBe('archived_conflict');
    });

    /** A 409 with no code is still a conflict, and re-reading is the remedy that fits an unknown one. */
    it('treats an uncoded 409 as a stale token', async () => {
      const pending = service.updateTestRun('cozy-fall', 'r1', RUN_ID, update());
      http.expectOne(`${BASE}/${RUN_ID}`).flush({}, { status: 409, statusText: 'Conflict' });

      expect((await pending).status).toBe('conflict');
    });

    it('reports an unknown test as not found', async () => {
      const pending = service.updateTestRun('cozy-fall', 'r1', RUN_ID, update());
      http.expectOne(`${BASE}/${RUN_ID}`).flush({}, { status: 404, statusText: 'Not Found' });

      expect((await pending).status).toBe('not_found');
    });

    it('carries indexed field errors through untouched, so a row can be found again', async () => {
      const pending = service.updateTestRun('cozy-fall', 'r1', RUN_ID, update());
      http.expectOne(`${BASE}/${RUN_ID}`).flush(
        { errors: { 'issues[1].severity': ['Say how badly this affects the recipe.'] } },
        { status: 400, statusText: 'Bad Request' },
      );

      const outcome = await pending;
      expect(outcome.status).toBe('validation_failed');
      if (outcome.status === 'validation_failed') {
        expect(outcome.fieldErrors['issues[1].severity']).toEqual(['Say how badly this affects the recipe.']);
      }
    });
  });

  /** No gateway means no address to send to, and nothing is attempted. */
  describe('listTestRuns', () => {
    it('reads the history from the workspace-scoped route, with credentials', async () => {
      const pending = firstValueFrom(service.listTestRuns('cozy-fall', 'r1', {}));

      const request = http.expectOne((candidate) => candidate.url === BASE);
      expect(request.request.method).toBe('GET');
      expect(request.request.withCredentials).toBeTrue();
      request.flush({ items: [], nextCursor: null, summary: null });

      const outcome = await pending;
      expect(outcome.status).toBe('found');
    });

    it('puts the filters on the query string as the route names them', async () => {
      const pending = firstValueFrom(
        service.listTestRuns('cozy-fall', 'r1', {
          versions: '3, 5',
          outcomes: ['Succeeded', 'Failed'],
          issues: 'HasUnresolved',
          includeSummary: false,
          cursor: 'c1',
        }),
      );

      const request = http.expectOne((candidate) => candidate.url === BASE);
      expect(request.request.params.get('version')).toBe('3, 5');
      expect(request.request.params.get('outcome')).toBe('Succeeded,Failed');
      expect(request.request.params.get('issues')).toBe('HasUnresolved');
      expect(request.request.params.get('includeSummary')).toBe('false');
      expect(request.request.params.get('cursor')).toBe('c1');
      request.flush({ items: [], nextCursor: null, summary: null });

      await pending;
    });

    /** Two 400s with two remedies: start the list again, or change the filter. */
    it('separates a stale cursor from a rejected filter', async () => {
      const cursor = firstValueFrom(service.listTestRuns('cozy-fall', 'r1', { cursor: 'c1' }));
      http
        .expectOne((candidate) => candidate.url === BASE)
        .flush({ code: 'recipes.cursor.invalid_request' }, { status: 400, statusText: 'Bad Request' });
      expect((await cursor).status).toBe('cursor_expired');

      const filter = firstValueFrom(service.listTestRuns('cozy-fall', 'r1', { versions: 'nine' }));
      http.expectOne((candidate) => candidate.url === BASE).flush(
        { code: 'recipes.testRun.invalid_request', errors: { version: ['A version number starts at 1.'] } },
        { status: 400, statusText: 'Bad Request' },
      );
      const filterOutcome = await filter;
      expect(filterOutcome.status).toBe('invalid_request');
      if (filterOutcome.status === 'invalid_request') {
        expect(filterOutcome.fieldErrors['version']).toEqual(['A version number starts at 1.']);
      }
    });

    /** Unlike the recipe search, a 404 here is real: it is the recipe, not an empty result. */
    it('reports an unreadable recipe as not found', async () => {
      const pending = firstValueFrom(service.listTestRuns('cozy-fall', 'r1', {}));
      http.expectOne((candidate) => candidate.url === BASE).flush({}, { status: 404, statusText: 'Not Found' });

      expect((await pending).status).toBe('not_found');
    });

    /** Every outcome is a value, so a failed page cannot kill the subscription. */
    it('never errors the stream', async () => {
      const pending = firstValueFrom(service.listTestRuns('cozy-fall', 'r1', {}));
      http
        .expectOne((candidate) => candidate.url === BASE)
        .flush({}, { status: 500, statusText: 'Server Error' });

      expect((await pending).status).toBe('unavailable');
    });
  });

  describe('resolveIssue', () => {
    const RESOLUTION_URL = `${BASE}/${RUN_ID}/issues/i1/resolution`;

    function resolvedPayload(): Record<string, unknown> {
      return {
        testRunId: RUN_ID,
        testIssueId: 'i1',
        resolution: {
          id: 'x1',
          kind: 'Fixed',
          notes: 'Added five minutes',
          resolvedByMembershipId: 'm2',
          resolvedAt: '2026-09-29T09:00:00Z',
          resolutionRecipeVersionId: 'v4',
          predatingVersionOverrideReason: null,
        },
      };
    }

    it('posts to the issue’s own resolution path', async () => {
      const pending = service.resolveIssue('cozy-fall', 'r1', RUN_ID, 'i1', { kind: 'Fixed' }, 'key-1');

      const request = http.expectOne(RESOLUTION_URL);
      expect(request.request.method).toBe('POST');
      expect(request.request.withCredentials).toBeTrue();
      expect(request.request.headers.get('Idempotency-Key')).toBe('key-1');
      request.flush(resolvedPayload());

      const outcome = await pending;
      expect(outcome.status).toBe('resolved');
      if (outcome.status === 'resolved') expect(outcome.resolved.resolution.kind).toBe('Fixed');
    });

    it('carries a 400 through as field errors', async () => {
      const pending = service.resolveIssue('cozy-fall', 'r1', RUN_ID, 'i1', { kind: 'WontFix' });
      http.expectOne(RESOLUTION_URL).flush(
        { errors: { resolutionVersionNumber: ['Only a fix names the version that corrected it.'] } },
        { status: 400, statusText: 'Bad Request' },
      );

      const outcome = await pending;
      expect(outcome.status).toBe('validation_failed');
    });

    it('tells a missing version apart from an unknown issue', async () => {
      const version = service.resolveIssue('cozy-fall', 'r1', RUN_ID, 'i1', { kind: 'Fixed' });
      http.expectOne(RESOLUTION_URL).flush(
        { code: 'recipes.version.not_found', errors: { resolutionVersionNumber: ['No such version.'] } },
        { status: 404, statusText: 'Not Found' },
      );
      expect((await version).status).toBe('version_not_found');

      const issue = service.resolveIssue('cozy-fall', 'r1', RUN_ID, 'i1', { kind: 'Fixed' });
      http
        .expectOne(RESOLUTION_URL)
        .flush({ code: 'recipes.testIssue.not_found' }, { status: 404, statusText: 'Not Found' });
      expect((await issue).status).toBe('not_found');
    });

    it('reports an issue already resolved, which no retry can clear', async () => {
      const pending = service.resolveIssue('cozy-fall', 'r1', RUN_ID, 'i1', { kind: 'Fixed' });
      http
        .expectOne(RESOLUTION_URL)
        .flush({ code: 'recipes.testIssue.resolved.conflict' }, { status: 409, statusText: 'Conflict' });

      expect((await pending).status).toBe('already_resolved');
    });

    it('reports the Editor bar as forbidden', async () => {
      const pending = service.resolveIssue('cozy-fall', 'r1', RUN_ID, 'i1', { kind: 'Fixed' });
      http.expectOne(RESOLUTION_URL).flush({}, { status: 403, statusText: 'Forbidden' });

      expect((await pending).status).toBe('forbidden');
    });

    it('reports a reused key as its own outcome', async () => {
      const pending = service.resolveIssue('cozy-fall', 'r1', RUN_ID, 'i1', { kind: 'Fixed' }, 'key-1');
      http.expectOne(RESOLUTION_URL).flush({}, { status: 422, statusText: 'Unprocessable Content' });

      expect((await pending).status).toBe('idempotency_key_conflict');
    });
  });

  describe('with no gateway resolved', () => {
    beforeEach(() => {
      TestBed.resetTestingModule();
      TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
      http = TestBed.inject(HttpTestingController);
      service = TestBed.inject(RecipeTestRunService);
    });

    it('reports unavailable without making a request', async () => {
      expect((await service.createTestRun('cozy-fall', 'r1', CREATE)).status).toBe('unavailable');
      expect((await service.updateTestRun('cozy-fall', 'r1', RUN_ID, update())).status).toBe('unavailable');
      http.expectNone(() => true);
    });
  });
});
