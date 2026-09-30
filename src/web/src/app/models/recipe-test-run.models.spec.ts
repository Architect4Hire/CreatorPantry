import {
  CreateRecipeTestRunRequest,
  UpdateRecipeTestRunRequest,
  absent,
  decodeCreatedRecipeTestRun,
  decodeRecipeTestRun,
  decodeResolvedTestIssue,
  decodeTestRunHistoryPage,
  encodeCreateRecipeTestRunRequest,
  encodeResolveTestIssueRequest,
  encodeTestRunHistoryQuery,
  encodeUpdateRecipeTestRunRequest,
  submitted,
} from './recipe-test-run.models';

const RUN_PAYLOAD: Record<string, unknown> = {
  id: 't1',
  recipeId: 'r1',
  recipeVersionId: 'v3',
  testedAt: '2026-09-28T18:00:00Z',
  testedByMembershipId: 'm1',
  outcome: 'SucceededWithIssues',
  rating: 4,
  environmentNotes: 'Fan oven, humid day',
  equipmentNotes: 'Dark metal tin',
  summaryNotes: 'Good crumb, slightly over-baked',
  actualYieldText: 'Got 10, not 12',
  actualYieldQuantity: 10,
  actualYieldUnitId: 'u1',
  actualPrepTimeMinutes: 20,
  actualCookTimeMinutes: 35,
  actualRestTimeMinutes: 10,
  actualTotalTimeMinutes: 55,
  createdByMembershipId: 'm1',
  updatedByMembershipId: 'm1',
  createdAt: '2026-09-28T19:00:00Z',
  updatedAt: '2026-09-28T19:00:00Z',
  concurrencyToken: 'token-1',
  observations: [{ id: 'o1', kind: 'Texture', text: 'Dense crumb', sortOrder: 0 }],
  issues: [
    {
      id: 'i1',
      severity: 'Major',
      title: 'Bake time too short',
      description: null,
      testObservationId: 'o1',
      sortOrder: 0,
      resolution: null,
    },
  ],
};

const CREATED_PAYLOAD: Record<string, unknown> = {
  testRunId: 't1',
  recipeId: 'r1',
  recipeVersionId: 'v3',
  sourceVersionNumber: 3,
  outcome: 'Succeeded',
  testedAt: '2026-09-28T18:00:00Z',
  createdAt: '2026-09-28T19:00:00Z',
  concurrencyToken: 'token-1',
  observationIds: ['o1', 'o2'],
  issueIds: ['i1'],
};

const MINIMAL_CREATE: CreateRecipeTestRunRequest = {
  sourceVersionNumber: 3,
  testedAt: '2026-09-28T18:00:00Z',
};

function emptyUpdate(): UpdateRecipeTestRunRequest {
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
  };
}

describe('recipe-test-run.models', () => {
  describe('encodeCreateRecipeTestRunRequest', () => {
    it('sends the version and the date, and every unset field as null', () => {
      const body = encodeCreateRecipeTestRunRequest(MINIMAL_CREATE);

      expect(body['sourceVersionNumber']).toBe(3);
      expect(body['testedAt']).toBe('2026-09-28T18:00:00Z');
      expect(body['rating']).toBeNull();
      expect(body['environmentNotes']).toBeNull();
      expect(body['actualTotalTimeMinutes']).toBeNull();
    });

    /** The lists are always present, so "no notes" is an empty list rather than a field the server must guess at. */
    it('sends empty lists rather than omitting them', () => {
      const body = encodeCreateRecipeTestRunRequest(MINIMAL_CREATE);

      expect(body['observations']).toEqual([]);
      expect(body['issues']).toEqual([]);
    });

    /** On a create nothing exists for an id to name, and the server refuses one. */
    it('omits a row id rather than sending null for it', () => {
      const body = encodeCreateRecipeTestRunRequest({
        ...MINIMAL_CREATE,
        observations: [{ kind: 'Texture', text: 'Dense crumb' }],
        issues: [{ severity: 'Minor', title: 'Slightly dry', observationIndex: 0 }],
      });

      const observations = body['observations'] as readonly Record<string, unknown>[];
      const issues = body['issues'] as readonly Record<string, unknown>[];

      expect('id' in observations[0]).toBeFalse();
      expect('id' in issues[0]).toBeFalse();
      expect(issues[0]['observationIndex']).toBe(0);
    });

    /** An unanswered severity is null, which the validator names; it is never an invented placeholder. */
    it('sends an unanswered severity as null', () => {
      const body = encodeCreateRecipeTestRunRequest({
        ...MINIMAL_CREATE,
        issues: [{ severity: null, title: '' }],
      });

      expect((body['issues'] as readonly Record<string, unknown>[])[0]['severity']).toBeNull();
    });
  });

  describe('encodeUpdateRecipeTestRunRequest', () => {
    /** Merge-patch semantics: an absent key means "leave it alone", so it must not appear at all. */
    it('omits every absent field, carrying only the token', () => {
      const body = encodeUpdateRecipeTestRunRequest(emptyUpdate());

      expect(Object.keys(body)).toEqual(['expectedConcurrencyToken']);
    });

    it('sends a submitted null to clear a field', () => {
      const body = encodeUpdateRecipeTestRunRequest({ ...emptyUpdate(), rating: submitted(null) });

      expect('rating' in body).toBeTrue();
      expect(body['rating']).toBeNull();
    });

    it('sends a submitted value', () => {
      const body = encodeUpdateRecipeTestRunRequest({
        ...emptyUpdate(),
        summaryNotes: submitted('Better on the second bake'),
        outcome: submitted('Succeeded'),
      });

      expect(body['summaryNotes']).toBe('Better on the second bake');
      expect(body['outcome']).toBe('Succeeded');
    });

    /** `[]` clears the list, which is a different instruction from leaving it alone. */
    it('tells an emptied list apart from an untouched one', () => {
      const cleared = encodeUpdateRecipeTestRunRequest({ ...emptyUpdate(), observations: submitted([]) });
      const untouched = encodeUpdateRecipeTestRunRequest(emptyUpdate());

      expect(cleared['observations']).toEqual([]);
      expect('observations' in untouched).toBeFalse();
    });

    it('keeps a row id when one is editing an existing note in place', () => {
      const body = encodeUpdateRecipeTestRunRequest({
        ...emptyUpdate(),
        observations: submitted([{ id: 'o1', kind: 'Flavour', text: 'Too salty' }]),
      });

      expect((body['observations'] as readonly Record<string, unknown>[])[0]['id']).toBe('o1');
    });

    /** There is no such field on the route, and nothing may invent one. */
    it('never sends a source version', () => {
      const body = encodeUpdateRecipeTestRunRequest({ ...emptyUpdate(), testedAt: submitted('2026-09-29T09:00:00Z') });

      expect('sourceVersionNumber' in body).toBeFalse();
    });
  });

  describe('decodeRecipeTestRun', () => {
    it('reads a whole run, its notes and its issues', () => {
      const run = decodeRecipeTestRun(RUN_PAYLOAD);

      expect(run).not.toBeNull();
      expect(run?.outcome).toBe('SucceededWithIssues');
      expect(run?.observations[0].kind).toBe('Texture');
      expect(run?.issues[0].severity).toBe('Major');
      expect(run?.issues[0].testObservationId).toBe('o1');
      expect(run?.issues[0].resolution).toBeNull();
    });

    it('reads a resolution when one has been recorded', () => {
      const run = decodeRecipeTestRun({
        ...RUN_PAYLOAD,
        issues: [
          {
            ...(RUN_PAYLOAD['issues'] as readonly Record<string, unknown>[])[0],
            resolution: {
              id: 'x1',
              kind: 'Fixed',
              notes: 'Added five minutes',
              resolvedByMembershipId: 'm2',
              resolvedAt: '2026-09-29T09:00:00Z',
              resolutionRecipeVersionId: 'v4',
              predatingVersionOverrideReason: null,
            },
          },
        ],
      });

      expect(run?.issues[0].resolution?.kind).toBe('Fixed');
      expect(run?.issues[0].resolution?.resolutionRecipeVersionId).toBe('v4');
    });

    it('rejects an unknown outcome rather than guessing at one', () => {
      expect(decodeRecipeTestRun({ ...RUN_PAYLOAD, outcome: 'Brilliant' })).toBeNull();
    });

    it('rejects an unknown severity rather than showing the issue without one', () => {
      expect(
        decodeRecipeTestRun({ ...RUN_PAYLOAD, issues: [{ id: 'i1', severity: 'Catastrophic', title: 'x', sortOrder: 0 }] }),
      ).toBeNull();
    });

    /**
     * A resolution that will not decode fails the whole run. Showing the issue as unresolved instead would let
     * an edit drop it, and the server's refusal would then be the only record that a decision ever existed.
     */
    it('rejects a run whose resolution cannot be read', () => {
      const run = decodeRecipeTestRun({
        ...RUN_PAYLOAD,
        issues: [
          {
            ...(RUN_PAYLOAD['issues'] as readonly Record<string, unknown>[])[0],
            resolution: { id: 'x1', kind: 'Reworded', resolvedByMembershipId: 'm2', resolvedAt: 'now' },
          },
        ],
      });

      expect(run).toBeNull();
    });

    it('rejects a missing concurrency token, which the next edit cannot do without', () => {
      const { concurrencyToken, ...withoutToken } = RUN_PAYLOAD;
      void concurrencyToken;

      expect(decodeRecipeTestRun(withoutToken)).toBeNull();
    });

    it('rejects a non-record', () => {
      expect(decodeRecipeTestRun('a test run')).toBeNull();
      expect(decodeRecipeTestRun(null)).toBeNull();
    });
  });

  describe('decodeCreatedRecipeTestRun', () => {
    it('reads the ids in submitted order, which is what pairs them with the rows', () => {
      const created = decodeCreatedRecipeTestRun(CREATED_PAYLOAD);

      expect(created?.testRunId).toBe('t1');
      expect(created?.observationIds).toEqual(['o1', 'o2']);
      expect(created?.issueIds).toEqual(['i1']);
      expect(created?.sourceVersionNumber).toBe(3);
    });

    it('rejects an id list holding something that is not an id', () => {
      expect(decodeCreatedRecipeTestRun({ ...CREATED_PAYLOAD, observationIds: ['o1', 7] })).toBeNull();
    });

    it('rejects a missing token', () => {
      expect(decodeCreatedRecipeTestRun({ ...CREATED_PAYLOAD, concurrencyToken: null })).toBeNull();
    });
  });

  describe('encodeTestRunHistoryQuery', () => {
    it('sends nothing at all for an empty query', () => {
      expect(encodeTestRunHistoryQuery({})).toEqual({});
    });

    it('comma-joins the multi-valued filters', () => {
      const params = encodeTestRunHistoryQuery({
        outcomes: ['Succeeded', 'Failed'],
        testedBy: ['m1', 'm2'],
      });

      expect(params['outcome']).toBe('Succeeded,Failed');
      expect(params['testedBy']).toBe('m1,m2');
    });

    it('sends the version text as typed, for the server to split', () => {
      expect(encodeTestRunHistoryQuery({ versions: ' 3, 5 ' })['version']).toBe('3, 5');
    });

    it('omits a filter that is set to nothing', () => {
      const params = encodeTestRunHistoryQuery({
        versions: '   ',
        outcomes: [],
        testedBy: [],
        issues: null,
        testedFrom: null,
        cursor: null,
      });

      expect(params).toEqual({});
    });

    /** True is the server's default, so sending it would be noise on every first page. */
    it('sends includeSummary only when it is false', () => {
      expect('includeSummary' in encodeTestRunHistoryQuery({ includeSummary: true })).toBeFalse();
      expect(encodeTestRunHistoryQuery({ includeSummary: false })['includeSummary']).toBe('false');
    });

    /** Mirroring a page size here would be one more number to keep in step with the server for no gain. */
    it('never sends a limit', () => {
      expect('limit' in encodeTestRunHistoryQuery({ cursor: 'c1', includeSummary: false })).toBeFalse();
    });
  });

  describe('decodeTestRunHistoryPage', () => {
    const SUMMARY_ROW: Record<string, unknown> = {
      id: 't1',
      recipeVersionId: 'v3',
      versionNumber: 3,
      testedAt: '2026-09-28T18:00:00Z',
      testedByMembershipId: 'm1',
      testedByName: 'Sam Okafor',
      outcome: 'SucceededWithIssues',
      rating: 4,
      summaryNotes: 'Good crumb',
      actualYieldText: null,
      actualYieldQuantity: null,
      actualYieldUnitId: null,
      actualPrepTimeMinutes: null,
      actualCookTimeMinutes: null,
      actualRestTimeMinutes: null,
      actualTotalTimeMinutes: 55,
      observationCount: 2,
      issueCount: 1,
      unresolvedIssueCount: 1,
      attachmentCount: 0,
      createdAt: '2026-09-28T19:00:00Z',
      updatedAt: '2026-09-28T19:00:00Z',
      concurrencyToken: 'token-1',
    };

    it('reads a page, its rows and its counts', () => {
      const decoded = decodeTestRunHistoryPage({
        items: [SUMMARY_ROW],
        nextCursor: 'c1',
        summary: {
          totalCount: 4,
          byOutcome: { Succeeded: 2, SucceededWithIssues: 1, Failed: 1, NotStated: 0 },
          runsWithUnresolvedIssues: 1,
          unresolvedIssueCount: 3,
        },
      });

      expect(decoded?.items[0].versionNumber).toBe(3);
      expect(decoded?.items[0].unresolvedIssueCount).toBe(1);
      expect(decoded?.nextCursor).toBe('c1');
      expect(decoded?.summary?.byOutcome['Succeeded']).toBe(2);
    });

    it('reads a page whose counts were not asked for', () => {
      const decoded = decodeTestRunHistoryPage({ items: [], nextCursor: null, summary: null });

      expect(decoded?.summary).toBeNull();
      expect(decoded?.items).toEqual([]);
    });

    /** A tester whose membership has gone still has a row; the name is what is missing, not the test. */
    it('reads a row whose tester can no longer be named', () => {
      const decoded = decodeTestRunHistoryPage({
        items: [{ ...SUMMARY_ROW, testedByName: null }],
        nextCursor: null,
        summary: null,
      });

      expect(decoded?.items[0].testedByName).toBeNull();
    });

    it('rejects a row with an unknown outcome rather than dropping it', () => {
      const decoded = decodeTestRunHistoryPage({
        items: [{ ...SUMMARY_ROW, outcome: 'Brilliant' }],
        nextCursor: null,
        summary: null,
      });

      expect(decoded).toBeNull();
    });

    it('rejects a count that is not a number', () => {
      const decoded = decodeTestRunHistoryPage({
        items: [{ ...SUMMARY_ROW, unresolvedIssueCount: 'one' }],
        nextCursor: null,
        summary: null,
      });

      expect(decoded).toBeNull();
    });

    it('rejects a summary whose breakdown holds something that is not a count', () => {
      const decoded = decodeTestRunHistoryPage({
        items: [],
        nextCursor: null,
        summary: { totalCount: 1, byOutcome: { Succeeded: 'two' }, runsWithUnresolvedIssues: 0, unresolvedIssueCount: 0 },
      });

      expect(decoded).toBeNull();
    });

    it('rejects a non-record and a missing items array', () => {
      expect(decodeTestRunHistoryPage('a page')).toBeNull();
      expect(decodeTestRunHistoryPage({ nextCursor: null })).toBeNull();
    });
  });

  describe('encodeResolveTestIssueRequest', () => {
    it('sends the kind and nulls everything not supplied', () => {
      expect(encodeResolveTestIssueRequest({ kind: 'WontFix' })).toEqual({
        kind: 'WontFix',
        notes: null,
        resolutionVersionNumber: null,
        predatingVersionOverrideReason: null,
      });
    });

    it('sends a correction version and the reason an earlier one was accepted', () => {
      const body = encodeResolveTestIssueRequest({
        kind: 'Fixed',
        notes: 'Added five minutes',
        resolutionVersionNumber: 2,
        predatingVersionOverrideReason: 'Version 2 had it right all along.',
      });

      expect(body['resolutionVersionNumber']).toBe(2);
      expect(body['predatingVersionOverrideReason']).toBe('Version 2 had it right all along.');
    });

    /** Nothing here describes the issue: the issue says what went wrong, this says what was done. */
    it('never restates the problem', () => {
      const body = encodeResolveTestIssueRequest({ kind: 'Fixed' });

      expect('severity' in body).toBeFalse();
      expect('title' in body).toBeFalse();
      expect('expectedConcurrencyToken' in body).toBeFalse();
    });
  });

  describe('decodeResolvedTestIssue', () => {
    it('reads the recorded resolution', () => {
      const decoded = decodeResolvedTestIssue({
        testRunId: 't1',
        testIssueId: 'i1',
        resolution: {
          id: 'x1',
          kind: 'Fixed',
          notes: null,
          resolvedByMembershipId: 'm2',
          resolvedAt: '2026-09-29T09:00:00Z',
          resolutionRecipeVersionId: 'v4',
          predatingVersionOverrideReason: null,
        },
      });

      expect(decoded?.testIssueId).toBe('i1');
      expect(decoded?.resolution.resolutionRecipeVersionId).toBe('v4');
    });

    it('rejects a resolution it cannot read', () => {
      expect(
        decodeResolvedTestIssue({ testRunId: 't1', testIssueId: 'i1', resolution: { id: 'x1', kind: 'Maybe' } }),
      ).toBeNull();
    });

    it('rejects a missing issue id', () => {
      expect(decodeResolvedTestIssue({ testRunId: 't1', resolution: null })).toBeNull();
    });
  });
});
