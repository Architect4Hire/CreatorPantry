import { EMPTY, Observable, defer, expand, switchMap, timer } from 'rxjs';

import { AiProposalStatus, isTerminalAiStatus } from '../../models/ai-proposal.models';

/** How often to ask while everything is answering. */
export const AI_POLL_INTERVAL_MS = 2000;

/** How often to ask once a poll has failed, so an outage is not hammered. */
export const AI_DEGRADED_POLL_INTERVAL_MS = 8000;

/** How many failures in a row end the loop. The last known state stays on screen. */
export const AI_MAX_CONSECUTIVE_POLL_FAILURES = 3;

/** The longest one loop will run. A generation that has not finished by now is not going to while we watch. */
export const AI_POLL_CEILING_MS = 5 * 60 * 1000;

/**
 * What one poll came back with, as far as the loop is concerned.
 *
 * Every typed AI client already answers in these three shapes, so a loop can be written once over them rather
 * than per capability. `found` is a reading; `unavailable` is a reading that did not arrive; anything else ends
 * the loop, because a request that is gone or refused will not become present or allowed by asking again.
 */
export type AiWatchOutcome<T> =
  | { readonly status: 'found'; readonly operation: T }
  | { readonly status: 'unavailable' }
  | { readonly status: 'not_found' }
  | { readonly status: 'forbidden' };

/**
 * True while there is still something to learn about an operation.
 *
 * `Proposed` is terminal **for these three capabilities**: there is no disposition route for a photography
 * concept, a composed prompt or a reference reading, so nothing moves server-side once the proposal exists.
 */
export function needsWatching(operation: AiProposalStatus): boolean {
  return !isTerminalAiStatus(operation.status) && operation.status !== 'Proposed';
}

/**
 * Asks once, then keeps asking until there is nothing left to learn.
 *
 * **Why this exists.** Three screens in `features/ai/` had already written this loop — the interval, the
 * degraded backoff, the consecutive-failure count and the wall-clock ceiling — and the rules only stay in step
 * by being in one place. Those three are left as they are; this is for the Content Pipeline's three panels, and
 * they could adopt it later without changing what any of them does.
 *
 * **It never errors.** Each client's watch answers with a value for every outcome, which is what stops one
 * failed poll from tearing down a loop and blanking content a creator is reading.
 *
 * **`onComplete` fires once the loop has stopped on its own**, so a caller can say "no longer checking" rather
 * than leaving a spinner up forever. Unsubscribing does not call it: that is the caller's own decision and they
 * already know.
 */
export function pollAiOperation<T extends AiProposalStatus>(
  ask: () => Observable<AiWatchOutcome<T>>,
  onComplete: () => void,
  now: () => number = Date.now,
): Observable<AiWatchOutcome<T>> {
  const deadline = now() + AI_POLL_CEILING_MS;
  let failures = 0;

  const again = (outcome: AiWatchOutcome<T>): Observable<AiWatchOutcome<T>> => {
    if (outcome.status === 'not_found' || outcome.status === 'forbidden') return EMPTY;
    if (outcome.status === 'found' && !needsWatching(outcome.operation)) return EMPTY;

    failures = outcome.status === 'unavailable' ? failures + 1 : 0;
    if (failures >= AI_MAX_CONSECUTIVE_POLL_FAILURES) return EMPTY;
    if (now() >= deadline) return EMPTY;

    return timer(failures === 0 ? AI_POLL_INTERVAL_MS : AI_DEGRADED_POLL_INTERVAL_MS).pipe(switchMap(ask));
  };

  return defer(ask).pipe(
    expand(again),
    (source) =>
      new Observable<AiWatchOutcome<T>>((subscriber) =>
        source.subscribe({
          next: (value) => subscriber.next(value),
          error: (error: unknown) => subscriber.error(error),
          complete: () => {
            onComplete();
            subscriber.complete();
          },
        }),
      ),
  );
}
