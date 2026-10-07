import { HttpErrorResponse } from '@angular/common/http';
import { Observable, Subject } from 'rxjs';

import { AiProposalStatus } from '../../models/ai-proposal.models';
import { AiRequestOutcome, AiWatchOperationOutcome, mapAiRequestError } from '../../services/ai-request';
import { AiOperationTracker } from './ai-operation-tracker';

function operation(overrides: Partial<AiProposalStatus> = {}): AiProposalStatus {
  return {
    aiProposalRequestId: 'r-1',
    status: 'Requested',
    taskType: 'PhotographyConcept',
    scope: 'Unspecified',
    sourceVersionId: null,
    requestedAt: '2026-10-07T12:00:00Z',
    statusChangedAt: '2026-10-07T12:00:00Z',
    failureCategory: null,
    proposal: null,
    ...overrides,
  };
}

const PROPOSAL: AiProposalStatus['proposal'] = {
  proposalId: 'p-1',
  outputSchemaVersion: '1',
  promptTemplateId: 't',
  promptTemplateVersion: '1',
  promptTemplateBodyChecksum: 'abc',
  providerName: 'provider',
  modelName: 'model',
  createdAt: '2026-10-07T12:00:05Z',
  changes: [],
  warnings: [],
};

describe('AiOperationTracker', () => {
  let watches: Subject<AiWatchOperationOutcome>[];
  let watched: string[];
  let tracker: AiOperationTracker;

  function watch(requestId: string): Observable<AiWatchOperationOutcome> {
    watched.push(requestId);
    const subject = new Subject<AiWatchOperationOutcome>();
    watches.push(subject);
    return subject.asObservable();
  }

  beforeEach(() => {
    watches = [];
    watched = [];
    tracker = new AiOperationTracker(watch);
  });

  afterEach(() => tracker.destroy());

  const accepted = (status: AiProposalStatus = operation()): AiRequestOutcome => ({
    status: 'accepted',
    operation: status,
    replayed: false,
  });

  it('starts idle, with nothing watched', () => {
    expect(tracker.phase().kind).toBe('idle');
    expect(tracker.requestId()).toBeNull();
    expect(watched).toEqual([]);
  });

  it('keeps the request id, watches it, and reports where it has got to', async () => {
    const requestId = await tracker.submit(() => Promise.resolve(accepted()));

    expect(requestId).toBe('r-1');
    expect(tracker.requestId()).toBe('r-1');
    expect(tracker.phase().kind).toBe('watching');
    expect(watched).toEqual(['r-1']);

    watches[0].next({ status: 'found', operation: operation({ status: 'Proposed', proposal: PROPOSAL }) });

    expect(tracker.phase().kind).toBe('ready');
    expect(tracker.proposal()).toBe(PROPOSAL);
  });

  it('does not watch a request that already came back finished', async () => {
    await tracker.submit(() => Promise.resolve(accepted(operation({ status: 'Proposed', proposal: PROPOSAL }))));

    expect(tracker.phase().kind).toBe('ready');
    expect(watched).toEqual([]);
  });

  it('reads a failure as a failure, with the operation still on screen', async () => {
    await tracker.submit(() => Promise.resolve(accepted()));
    watches[0].next({ status: 'found', operation: operation({ status: 'Failed', failureCategory: 'Provider' }) });

    const phase = tracker.phase();
    expect(phase.kind).toBe('failed');
    if (phase.kind === 'failed') expect(phase.operation.failureCategory).toBe('Provider');
  });

  it('keeps the last reading when a poll does not arrive, and says the connection is degraded', async () => {
    await tracker.submit(() => Promise.resolve(accepted()));
    watches[0].next({ status: 'found', operation: operation({ status: 'Running' }) });
    expect(tracker.connection()).toBe('live');

    watches[0].next({ status: 'unavailable' });

    expect(tracker.connection()).toBe('degraded');
    // Still Running on screen: one failed poll must not blank what a creator is reading.
    const phase = tracker.phase();
    expect(phase.kind).toBe('watching');
    if (phase.kind === 'watching') expect(phase.operation?.status).toBe('Running');
  });

  it('reports a request that is gone, and one it may not read, differently', async () => {
    await tracker.submit(() => Promise.resolve(accepted()));
    watches[0].next({ status: 'not_found' });
    expect(tracker.phase().kind).toBe('gone');

    await tracker.submit(() => Promise.resolve(accepted()));
    watches[1].next({ status: 'forbidden' });
    expect(tracker.phase().kind).toBe('forbidden');
  });

  it('stops checking without claiming to have cancelled anything', async () => {
    await tracker.submit(() => Promise.resolve(accepted()));
    const subject = watches[0];

    tracker.stopChecking();

    expect(subject.observed).toBeFalse();
    expect(tracker.stopped()).toBeTrue();
    // The request id is kept: the generation is still running server-side and can be picked back up.
    expect(tracker.requestId()).toBe('r-1');
  });

  it('never lets an abandoned answer land on the one the creator is looking at', async () => {
    await tracker.submit(() => Promise.resolve(accepted(operation({ aiProposalRequestId: 'r-first' }))));
    const first = watches[0];

    await tracker.submit(() => Promise.resolve(accepted(operation({ aiProposalRequestId: 'r-second' }))));

    expect(first.observed).toBeFalse();
    expect(tracker.requestId()).toBe('r-second');

    // The first request answers late, with a finished proposal. It must change nothing.
    first.next({ status: 'found', operation: operation({ status: 'Proposed', proposal: PROPOSAL }) });

    expect(tracker.phase().kind).toBe('watching');
    expect(tracker.proposal()).toBeNull();
  });

  it('discards a submission the creator has already moved on from', async () => {
    let release!: (outcome: AiRequestOutcome) => void;
    const slow = new Promise<AiRequestOutcome>((resolve) => {
      release = resolve;
    });

    const pending = tracker.submit(() => slow);
    tracker.reset();

    release(accepted());
    expect(await pending).toBeNull();

    // Reset won: nothing from the abandoned ask is on screen, and nothing is being watched.
    expect(tracker.phase().kind).toBe('idle');
    expect(tracker.requestId()).toBeNull();
    expect(watched).toEqual([]);
  });

  it('unsubscribes the poll when it is destroyed', async () => {
    await tracker.submit(() => Promise.resolve(accepted()));
    const subject = watches[0];

    tracker.destroy();

    expect(subject.observed).toBeFalse();
  });

  it('picks an operation back up by its id', () => {
    tracker.resume('r-kept');

    expect(tracker.requestId()).toBe('r-kept');
    expect(tracker.phase().kind).toBe('watching');
    expect(watched).toEqual(['r-kept']);
  });

  it('maps every way an ask can be refused onto its own phase', async () => {
    const refusal = (error: unknown): Promise<AiRequestOutcome> =>
      Promise.resolve(mapAiRequestError(error, 'task.not_enabled', ['thing.not_found']));
    const problem = (status: number, body: Record<string, unknown>): HttpErrorResponse =>
      new HttpErrorResponse({ status, error: body });

    await tracker.submit(() => refusal(problem(400, { code: 'task.not_enabled' })));
    expect(tracker.phase().kind).toBe('not_enabled');

    await tracker.submit(() => refusal(problem(404, { code: 'thing.not_found', title: 'Gone.' })));
    const refused = tracker.phase();
    expect(refused.kind).toBe('refused');
    if (refused.kind === 'refused') expect(refused.message).toBe('Gone.');

    await tracker.submit(() => refusal(problem(400, { code: 'other', errors: { conceptId: ['Required.'] } })));
    const invalid = tracker.phase();
    expect(invalid.kind).toBe('refused');
    if (invalid.kind === 'refused') expect(invalid.fieldErrors['conceptId']).toEqual(['Required.']);

    await tracker.submit(() => refusal(problem(403, { code: 'ai.quota.suspended', unit: 'Credits' })));
    const blocked = tracker.phase();
    expect(blocked.kind).toBe('blocked');
    if (blocked.kind === 'blocked') expect(blocked.refusal.status).toBe('account_suspended');

    await tracker.submit(() => refusal(problem(403, { code: 'workspace.forbidden' })));
    expect(tracker.phase().kind).toBe('forbidden');

    await tracker.submit(() => refusal(problem(500, {})));
    expect(tracker.phase().kind).toBe('unavailable');

    // None of those watched anything: there is no operation to watch when there is no request.
    expect(watched).toEqual([]);
  });

  it('reports a reused key as its own answer, not as an outage', async () => {
    await tracker.submit(() => Promise.resolve<AiRequestOutcome>({ status: 'idempotency_key_conflict' }));

    // Its own phase because the remedy differs and is the creator's: ask again. Reported as an outage it would
    // look like something that might fix itself.
    expect(tracker.phase().kind).toBe('key_reused');
  });

  it('does not strand the screen when Proposed arrives with no proposal', async () => {
    await tracker.submit(() => Promise.resolve(accepted()));
    watches[0].next({ status: 'found', operation: operation({ status: 'Proposed' }) });

    // Polling stops at Proposed, so leaving this 'watching' would wait forever for something that cannot change.
    expect(tracker.phase().kind).toBe('unavailable');
    expect(tracker.busy()).toBeFalse();
  });

  it('is busy only while the creator is waiting', async () => {
    expect(tracker.busy()).toBeFalse();

    await tracker.submit(() => Promise.resolve(accepted()));
    expect(tracker.busy()).toBeTrue();

    watches[0].next({ status: 'found', operation: operation({ status: 'Proposed', proposal: PROPOSAL }) });
    expect(tracker.busy()).toBeFalse();
  });
});
