import { HttpErrorResponse } from '@angular/common/http';

import { decodeAiQuotaRefusal } from './ai-quota.models';

function refusal(status: number, body: Record<string, unknown>): HttpErrorResponse {
  return new HttpErrorResponse({ status, statusText: 'Refused', error: body });
}

describe('decodeAiQuotaRefusal', () => {
  it('reads a spent allowance, including what the request would have taken', () => {
    const decoded = decodeAiQuotaRefusal(
      refusal(429, {
        code: 'ai.quota.exhausted',
        title: 'Your AI allowance for this period is spent.',
        unit: 'Credits',
        allowance: 1000,
        remaining: 5,
        required: 20,
        resetsAt: '2026-10-01T00:00:00+00:00',
      }),
    );

    expect(decoded).toEqual({
      status: 'quota_exhausted',
      unit: 'Credits',
      allowance: 1000,
      remaining: 5,
      required: 20,
      resetsAt: '2026-10-01T00:00:00+00:00',
    });
  });

  // No balance and no reset: there is nothing to wait for, so nothing is offered to wait for.
  it('reads a suspension as its own outcome, carrying no reset', () => {
    const decoded = decodeAiQuotaRefusal(
      refusal(403, { code: 'ai.quota.suspended', title: 'Switched off.', unit: 'Credits' }),
    );

    expect(decoded).toEqual({ status: 'account_suspended', unit: 'Credits' });
  });

  /**
   * The reason this matches on the code rather than the status. A suspension shares its status with a role
   * refusal, and a spent allowance shares its status with the gateway's own rate limiter — so a decoder that
   * went by status would claim both of those were quota refusals.
   */
  it('does not claim a role refusal or an edge rate limit', () => {
    expect(decodeAiQuotaRefusal(refusal(403, { code: 'ai.disposition.forbidden' }))).toBeNull();
    expect(decodeAiQuotaRefusal(refusal(429, { code: 'rate_limited' }))).toBeNull();
  });

  /**
   * A refusal with no usable reset is left to the caller's generic failure path rather than reported with a
   * guessed one: "try again at some point" is worse than "something went wrong".
   */
  it('declines an exhausted refusal that cannot say when it lifts', () => {
    expect(
      decodeAiQuotaRefusal(refusal(429, { code: 'ai.quota.exhausted', unit: 'Credits', remaining: 0 })),
    ).toBeNull();
  });

  it('survives a body whose amounts are missing or the wrong shape', () => {
    const decoded = decodeAiQuotaRefusal(
      refusal(429, {
        code: 'ai.quota.exhausted',
        remaining: 'not a number',
        resetsAt: '2026-10-01T00:00:00+00:00',
      }),
    );

    expect(decoded).toEqual({
      status: 'quota_exhausted',
      unit: '',
      allowance: 0,
      remaining: 0,
      required: 0,
      resetsAt: '2026-10-01T00:00:00+00:00',
    });
  });

  it('ignores anything that is not an HTTP error at all', () => {
    expect(decodeAiQuotaRefusal(new Error('offline'))).toBeNull();
    expect(decodeAiQuotaRefusal(null)).toBeNull();
    expect(decodeAiQuotaRefusal(refusal(429, {}))).toBeNull();
  });
});
