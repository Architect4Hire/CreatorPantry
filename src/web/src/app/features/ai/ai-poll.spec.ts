import { fakeAsync, tick } from '@angular/core/testing';
import { Observable, Subject } from 'rxjs';

import { AiProposalStatus } from '../../models/ai-proposal.models';
import { AiWatchOperationOutcome } from '../../services/ai-request';
import {
  AI_DEGRADED_POLL_INTERVAL_MS,
  AI_MAX_CONSECUTIVE_POLL_FAILURES,
  AI_POLL_CEILING_MS,
  AI_POLL_INTERVAL_MS,
  needsWatching,
  pollAiOperation,
} from './ai-poll';

function operation(status: AiProposalStatus['status']): AiProposalStatus {
  return {
    aiProposalRequestId: 'r-1',
    status,
    taskType: 'PhotographyConcept',
    scope: 'Unspecified',
    sourceVersionId: null,
    requestedAt: '2026-10-07T12:00:00Z',
    statusChangedAt: '2026-10-07T12:00:00Z',
    failureCategory: null,
    proposal: null,
  };
}

describe('needsWatching', () => {
  it('stops at Proposed, because no disposition route moves these three capabilities on', () => {
    expect(needsWatching(operation('Requested'))).toBeTrue();
    expect(needsWatching(operation('Running'))).toBeTrue();
    expect(needsWatching(operation('Proposed'))).toBeFalse();
    expect(needsWatching(operation('Failed'))).toBeFalse();
    expect(needsWatching(operation('Expired'))).toBeFalse();
  });
});

describe('pollAiOperation', () => {
  let asks: Subject<AiWatchOperationOutcome>[];
  let completed: number;
  let now: number;

  function ask(): Observable<AiWatchOperationOutcome> {
    const subject = new Subject<AiWatchOperationOutcome>();
    asks.push(subject);
    return subject.asObservable();
  }

  function start(): { readonly seen: AiWatchOperationOutcome[]; readonly done: () => boolean } {
    const seen: AiWatchOperationOutcome[] = [];
    let finished = false;
    pollAiOperation(
      ask,
      () => {
        completed += 1;
        finished = true;
      },
      () => now,
    ).subscribe((outcome) => seen.push(outcome));

    return { seen, done: () => finished };
  }

  beforeEach(() => {
    asks = [];
    completed = 0;
    now = 0;
  });

  it('asks once immediately', () => {
    start();

    expect(asks.length).toBe(1);
  });

  it('stops the moment there is nothing left to learn', () => {
    const loop = start();

    asks[0].next({ status: 'found', operation: operation('Proposed') });
    asks[0].complete();

    expect(loop.seen.length).toBe(1);
    expect(loop.done()).toBeTrue();
    expect(completed).toBe(1);
  });

  it('stops on a request that is gone, and on one it may not read', () => {
    const gone = start();
    asks[0].next({ status: 'not_found' });
    asks[0].complete();
    expect(gone.done()).toBeTrue();

    const refused = start();
    asks[1].next({ status: 'forbidden' });
    asks[1].complete();
    expect(refused.done()).toBeTrue();
  });

  it('waits before asking again after a failure, rather than hammering an outage', fakeAsync(() => {
    start();

    asks[0].next({ status: 'unavailable' });
    asks[0].complete();

    // Nothing yet: the next ask is behind the degraded interval.
    expect(asks.length).toBe(1);

    tick(AI_DEGRADED_POLL_INTERVAL_MS);
    expect(asks.length).toBe(2);
  }));

  it('gives up after enough failures in a row', fakeAsync(() => {
    const loop = start();

    for (let attempt = 0; attempt < AI_MAX_CONSECUTIVE_POLL_FAILURES; attempt += 1) {
      asks[attempt].next({ status: 'unavailable' });
      asks[attempt].complete();
      tick(AI_DEGRADED_POLL_INTERVAL_MS);
    }

    expect(loop.done()).toBeTrue();
    // Each failure was still reported, so the screen could say the reading was stale.
    expect(loop.seen.length).toBe(AI_MAX_CONSECUTIVE_POLL_FAILURES);
    expect(asks.length).toBe(AI_MAX_CONSECUTIVE_POLL_FAILURES);
  }));

  it('goes back to the quick interval once a poll arrives again', fakeAsync(() => {
    start();

    asks[0].next({ status: 'unavailable' });
    asks[0].complete();
    tick(AI_DEGRADED_POLL_INTERVAL_MS);

    asks[1].next({ status: 'found', operation: operation('Running') });
    asks[1].complete();

    tick(AI_POLL_INTERVAL_MS);
    expect(asks.length).toBe(3);
  }));

  it('stops once it has been watching longer than one loop should', () => {
    const loop = start();

    now = AI_POLL_CEILING_MS + 1;
    asks[0].next({ status: 'found', operation: operation('Running') });
    asks[0].complete();

    expect(loop.done()).toBeTrue();
  });

  it('asks nothing more once the caller unsubscribes', () => {
    const seen: AiWatchOperationOutcome[] = [];
    const subscription = pollAiOperation(ask, () => (completed += 1), () => now).subscribe((outcome) =>
      seen.push(outcome),
    );

    subscription.unsubscribe();
    asks[0].next({ status: 'found', operation: operation('Proposed') });

    expect(seen).toEqual([]);
    // Unsubscribing is the caller's own decision, so it is not reported back to them as the loop finishing.
    expect(completed).toBe(0);
  });
});
