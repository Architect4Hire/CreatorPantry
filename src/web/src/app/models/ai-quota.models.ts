import { HttpErrorResponse } from '@angular/common/http';

/**
 * Why an AI request was refused before anything was queued (USAGE-006/007).
 *
 * Two different situations with two different remedies, which is why they are two outcomes rather than one
 * with a flag: an allowance comes back on its own at a known instant, and a suspension does not come back at
 * all. A UI that treated them the same would leave a suspended creator waiting for a reset that never arrives.
 */
export type AiQuotaRefusal =
  /**
   * `429`. The account's allowance for the current period will not cover this request. Everything here is the
   * caller's own account — it names no workspace and no other account.
   */
  | {
      readonly status: 'quota_exhausted';
      /** What the three amounts are counted in, e.g. `Credits`. */
      readonly unit: string;
      /** The period's spendable total: its allowance plus anything carried into it. */
      readonly allowance: number;
      /** What is left of that. */
      readonly remaining: number;
      /** What this request would have taken — without it, "you have 5 left" does not explain the refusal. */
      readonly required: number;
      /** When the allowance comes back, as an ISO 8601 instant. */
      readonly resetsAt: string;
    }
  /** `403`. AI is switched off for this account. No balance and no reset: there is nothing to wait for. */
  | { readonly status: 'account_suspended'; readonly unit: string };

/** `AiProposalErrors.QuotaExhausted`, mirrored. Renaming it server-side is a breaking API change. */
const QUOTA_EXHAUSTED_CODE = 'ai.quota.exhausted';

/** `AiProposalErrors.QuotaSuspended`, mirrored. */
const QUOTA_SUSPENDED_CODE = 'ai.quota.suspended';

/**
 * The quota refusal an error carries, or `null` when it is not one.
 *
 * Written once and shared by every request service, because all eight AI request routes refuse this way and
 * eight copies of the decoding would drift the first time a field was added. Matched on the stable `code`
 * rather than on the status: a suspension is a `403` like a role refusal, and an exhausted allowance is a
 * `429` like the edge's own rate limiter, so the status alone cannot tell any of them apart.
 */
export function decodeAiQuotaRefusal(error: unknown): AiQuotaRefusal | null {
  const body = error instanceof HttpErrorResponse ? (error.error as Record<string, unknown> | null) : null;
  const code = body?.['code'];

  if (code === QUOTA_SUSPENDED_CODE) {
    return { status: 'account_suspended', unit: textOf(body?.['unit']) };
  }

  if (code !== QUOTA_EXHAUSTED_CODE) return null;

  const resetsAt = body?.['resetsAt'];

  // A refusal that cannot say when it lifts is not one this type can honestly represent, so it is left to the
  // caller's generic failure path rather than reported with a guessed or empty reset.
  if (typeof resetsAt !== 'string') return null;

  return {
    status: 'quota_exhausted',
    unit: textOf(body?.['unit']),
    allowance: numberOf(body?.['allowance']),
    remaining: numberOf(body?.['remaining']),
    required: numberOf(body?.['required']),
    resetsAt,
  };
}

function textOf(value: unknown): string {
  return typeof value === 'string' ? value : '';
}

/** Zero for anything unreadable: an amount nobody can parse is not an amount worth showing. */
function numberOf(value: unknown): number {
  return typeof value === 'number' && Number.isFinite(value) ? value : 0;
}
