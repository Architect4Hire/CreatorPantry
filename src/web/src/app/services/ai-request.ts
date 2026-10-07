import { HttpErrorResponse } from '@angular/common/http';

import { AiQuotaRefusal, decodeAiQuotaRefusal } from '../models/ai-quota.models';
import { AiProposalStatus } from '../models/ai-proposal.models';

/**
 * What asking for one AI operation came back with.
 *
 * **Why this is shared.** The three image capabilities — concepts, prompt composition, reference reading —
 * answer with the same statuses in the same order of precedence, and the order is the part that is easy to get
 * wrong: a suspension arrives as a `403` like a role refusal and a spent allowance as a `429` like the edge's
 * own rate limiter, so only the problem code tells them apart. One mapping, tested once.
 *
 * `refused` is the capability's own 404/422 — a concept that cannot be composed from, a brief that is not this
 * workspace's — carried with the server's sentence rather than reworded, because the server is the one that
 * knows which pin failed.
 */
export type AiRequestOutcome =
  /** `202`: the request is durable and queued. Nothing is generated yet — poll for it. */
  | { readonly status: 'accepted'; readonly operation: AiProposalStatus; readonly replayed: boolean }
  /** A real task, switched off for this deployment. A configuration change, not a client fix. */
  | { readonly status: 'task_not_enabled' }
  | { readonly status: 'validation_failed'; readonly fieldErrors: Readonly<Record<string, readonly string[]>> }
  /** Something the request named cannot be used. `code` is the capability's own, `message` the server's words. */
  | { readonly status: 'refused'; readonly code: string; readonly message: string }
  /** The `Idempotency-Key` was reused for a different request. Generate a fresh one; do not re-send this. */
  | { readonly status: 'idempotency_key_conflict' }
  /** Requesting needs the Contributor role. */
  | { readonly status: 'forbidden' }
  /** The account cannot pay for this request: its allowance is spent, or AI is switched off for it. */
  | AiQuotaRefusal
  | { readonly status: 'unavailable' };

/** Where one request has got to. Never an error, so a polling loop cannot be killed by one failed poll. */
export type AiWatchOperationOutcome =
  | { readonly status: 'found'; readonly operation: AiProposalStatus }
  | { readonly status: 'not_found' }
  | { readonly status: 'forbidden' }
  | { readonly status: 'unavailable' };

export function statusCodeOf(error: unknown): number | null {
  return error instanceof HttpErrorResponse ? error.status : null;
}

/** The stable `code` extension on a ProblemDetails body — see `ProblemResults.StatusFor` server-side. */
export function problemCodeOf(error: unknown): string | null {
  const body = error instanceof HttpErrorResponse ? (error.error as Record<string, unknown> | null) : null;
  const code = body?.['code'];

  return typeof code === 'string' ? code : null;
}

/** `ProblemDetails.title`, which carries the server's own sentence about a refusal. */
export function problemMessageOf(error: unknown): string | null {
  const body = error instanceof HttpErrorResponse ? (error.error as Record<string, unknown> | null) : null;
  const title = body?.['title'];

  return typeof title === 'string' && title.trim() !== '' ? title : null;
}

/** `ValidationProblemDetails.errors`, keyed by camelCase request field name. */
export function decodeFieldErrors(error: unknown): Record<string, readonly string[]> {
  const body = error instanceof HttpErrorResponse ? (error.error as Record<string, unknown> | null) : null;
  const errors = body?.['errors'];
  if (typeof errors !== 'object' || errors === null) return {};

  const result: Record<string, readonly string[]> = {};
  for (const [field, messages] of Object.entries(errors as Record<string, unknown>)) {
    if (Array.isArray(messages) && messages.every((message) => typeof message === 'string')) {
      result[field] = messages;
    }
  }

  return result;
}

/**
 * The accepted reply, or the one outcome this error means.
 *
 * `refusalCodes` are the capability's own — the ones that mean "something you named cannot be used" rather than
 * "your request is the wrong shape". They are matched before the generic fallbacks because they arrive as the
 * same statuses.
 */
export function mapAiRequestError(error: unknown, notEnabledCode: string, refusalCodes: readonly string[]): AiRequestOutcome {
  const code = statusCodeOf(error);
  const problem = problemCodeOf(error);

  if (problem !== null && refusalCodes.includes(problem)) {
    return { status: 'refused', code: problem, message: problemMessageOf(error) ?? 'That cannot be used here.' };
  }

  if (code === 400) {
    return problem === notEnabledCode
      ? { status: 'task_not_enabled' }
      : { status: 'validation_failed', fieldErrors: decodeFieldErrors(error) };
  }

  if (code === 422) return { status: 'idempotency_key_conflict' };

  // Before the 403 and 429 fallbacks: a suspension is a 403 like a role refusal and a spent allowance a 429
  // like the edge's rate limiter, so only the code tells them apart.
  const refusal = decodeAiQuotaRefusal(error);
  if (refusal) return refusal;

  if (code === 403) return { status: 'forbidden' };

  return { status: 'unavailable' };
}

/** The one outcome a failed poll means. */
export function mapAiWatchError(error: unknown): AiWatchOperationOutcome {
  const code = statusCodeOf(error);
  if (code === 404) return { status: 'not_found' };
  if (code === 403) return { status: 'forbidden' };

  return { status: 'unavailable' };
}
