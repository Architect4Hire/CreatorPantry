import { HttpErrorResponse } from '@angular/common/http';

import { mapAiRequestError, mapAiWatchError } from './ai-request';

const NOT_ENABLED = 'ai.imagePrompt.not_enabled';
const CONCEPT_GONE = 'ai.imagePromptConcept.not_found';
const BRIEF_GONE = 'ai.imagePromptBrief.not_found';

function problem(status: number, body: Record<string, unknown> | null): HttpErrorResponse {
  return new HttpErrorResponse({ status, error: body });
}

describe('mapAiRequestError', () => {
  const map = (error: unknown) => mapAiRequestError(error, NOT_ENABLED, [CONCEPT_GONE, BRIEF_GONE]);

  it('reads the capability’s own refusal, with the server’s sentence', () => {
    const outcome = map(problem(404, { code: CONCEPT_GONE, title: 'That look is no longer there.' }));

    expect(outcome.status).toBe('refused');
    if (outcome.status === 'refused') {
      expect(outcome.code).toBe(CONCEPT_GONE);
      expect(outcome.message).toBe('That look is no longer there.');
    }
  });

  it('words a refusal that arrived without a sentence, rather than showing a blank', () => {
    const outcome = map(problem(422, { code: BRIEF_GONE }));

    expect(outcome.status).toBe('refused');
    if (outcome.status === 'refused') expect(outcome.message.length).toBeGreaterThan(0);
  });

  it('tells a switched-off task from a badly shaped request, both of which are 400s', () => {
    expect(map(problem(400, { code: NOT_ENABLED })).status).toBe('task_not_enabled');

    const invalid = map(problem(400, { code: 'ai.imagePrompt.invalid_request', errors: { conceptId: ['Required.'] } }));
    expect(invalid.status).toBe('validation_failed');
    if (invalid.status === 'validation_failed') expect(invalid.fieldErrors['conceptId']).toEqual(['Required.']);
  });

  it('reads a 422 with no capability code as a reused idempotency key', () => {
    expect(map(problem(422, { code: 'idempotency.key_reused' })).status).toBe('idempotency_key_conflict');
  });

  it('tells a spent allowance from the edge’s rate limiter, which share a 429', () => {
    const spent = map(
      problem(429, {
        code: 'ai.quota.exhausted',
        unit: 'Credits',
        allowance: 100,
        remaining: 2,
        required: 10,
        resetsAt: '2026-11-01T00:00:00Z',
      }),
    );

    expect(spent.status).toBe('quota_exhausted');
    expect(map(problem(429, { code: 'edge.rate_limited' })).status).toBe('unavailable');
  });

  it('tells a suspended account from a role refusal, which share a 403', () => {
    expect(map(problem(403, { code: 'ai.quota.suspended', unit: 'Credits' })).status).toBe('account_suspended');
    expect(map(problem(403, { code: 'workspace.forbidden' })).status).toBe('forbidden');
  });

  it('reads anything else as unavailable', () => {
    expect(map(problem(500, null)).status).toBe('unavailable');
    expect(map(new Error('offline')).status).toBe('unavailable');
  });
});

describe('mapAiWatchError', () => {
  it('tells a request that is gone from one that is refused, and everything else from both', () => {
    expect(mapAiWatchError(problem(404, null)).status).toBe('not_found');
    expect(mapAiWatchError(problem(403, null)).status).toBe('forbidden');
    expect(mapAiWatchError(problem(500, null)).status).toBe('unavailable');
    expect(mapAiWatchError(new Error('offline')).status).toBe('unavailable');
  });
});

describe('mapAiRequestError and a refusal it cannot honestly represent', () => {
  it('reads an exhausted allowance with no reset time as unavailable rather than guessing one', () => {
    const outcome = mapAiRequestError(
      new HttpErrorResponse({ status: 429, error: { code: 'ai.quota.exhausted', unit: 'Credits' } }),
      NOT_ENABLED,
      [],
    );

    expect(outcome.status).toBe('unavailable');
  });
});
