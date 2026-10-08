import { Observable, Subject } from 'rxjs';

import { GeneratedImageOperationDetail, StagedImage } from '../../models/generated-image.models';
import { GeneratedImageRequestOutcome, GeneratedImageWatchOutcome } from '../../services/generated-image.service';
import { GeneratedImageTracker } from './generated-image-tracker';

function picture(overrides: Partial<StagedImage> = {}): StagedImage {
  return {
    id: 'img-1',
    variantIndex: 1,
    status: 'Staged',
    mediaType: 'image/png',
    width: 1024,
    height: 1024,
    sizeBytes: 482000,
    retentionExpiresAt: '2026-10-09T12:00:00Z',
    createdAt: '2026-10-08T12:00:00Z',
    ...overrides,
  };
}

function run(overrides: Partial<GeneratedImageOperationDetail> = {}): GeneratedImageOperationDetail {
  return {
    id: 'op-1',
    status: 'Requested',
    variantCount: 2,
    stagedCount: 0,
    providerName: null,
    modelName: null,
    failureCategory: null,
    failureSummary: null,
    requestedAt: '2026-10-08T12:00:00Z',
    completedAt: null,
    images: [],
    ...overrides,
  };
}

describe('GeneratedImageTracker', () => {
  let watches: Subject<GeneratedImageWatchOutcome>[];
  let watched: string[];
  let tracker: GeneratedImageTracker;

  function watch(operationId: string): Observable<GeneratedImageWatchOutcome> {
    watched.push(operationId);
    const subject = new Subject<GeneratedImageWatchOutcome>();
    watches.push(subject);

    return subject.asObservable();
  }

  beforeEach(() => {
    watches = [];
    watched = [];
    tracker = new GeneratedImageTracker(watch);
  });

  afterEach(() => tracker.destroy());

  /** The 202 body, which carries the run and no picture identities at all. */
  const accepted = (id = 'op-1'): GeneratedImageRequestOutcome => {
    const { images: _images, ...operation } = run({ id });

    return { status: 'accepted', operation };
  };

  it('starts idle, with nothing watched', () => {
    expect(tracker.phase().kind).toBe('idle');
    expect(tracker.operationId()).toBeNull();
    expect(watched).toEqual([]);
  });

  it('keeps the run id and watches it, because the accepted answer names no picture', async () => {
    const operationId = await tracker.submit(() => Promise.resolve(accepted()));

    expect(operationId).toBe('op-1');
    expect(tracker.operationId()).toBe('op-1');
    expect(tracker.phase().kind).toBe('watching');
    expect(tracker.detail()).withContext('nothing to show until the first read').toBeNull();
    expect(watched).toEqual(['op-1']);
  });

  it('fills a contact sheet in as pictures land rather than waiting for all of them', async () => {
    await tracker.submit(() => Promise.resolve(accepted()));

    watches[0].next({
      status: 'found',
      operation: run({ status: 'Running', stagedCount: 1, images: [picture()] }),
    });

    expect(tracker.phase().kind).toBe('watching');
    expect(tracker.detail()?.images.length).toBe(1);
  });

  it('reads a finished run as ready, with its pictures', async () => {
    await tracker.submit(() => Promise.resolve(accepted()));

    watches[0].next({
      status: 'found',
      operation: run({ status: 'Succeeded', stagedCount: 2, images: [picture(), picture({ id: 'img-2', variantIndex: 2 })] }),
    });

    const phase = tracker.phase();
    expect(phase.kind).toBe('ready');
    if (phase.kind === 'ready') expect(phase.operation.images.length).toBe(2);
  });

  it('treats a part-finished run as ready, not as a failure: the pictures that landed are the point', async () => {
    await tracker.submit(() => Promise.resolve(accepted()));

    watches[0].next({
      status: 'found',
      operation: run({ status: 'PartiallySucceeded', stagedCount: 1, images: [picture()] }),
    });

    expect(tracker.phase().kind).toBe('ready');
  });

  it('reads a run that produced nothing as failed, carrying the reason it was given', async () => {
    await tracker.submit(() => Promise.resolve(accepted()));

    watches[0].next({
      status: 'found',
      operation: run({ status: 'Failed', failureCategory: 'provider-refused', failureSummary: 'Turned down.' }),
    });

    const phase = tracker.phase();
    expect(phase.kind).toBe('failed');
    if (phase.kind === 'failed') {
      expect(phase.operation.failureCategory).toBe('provider-refused');
      expect(phase.operation.failureSummary).toBe('Turned down.');
    }
  });

  it('keeps the last reading when a poll does not arrive, and says the connection is degraded', async () => {
    await tracker.submit(() => Promise.resolve(accepted()));
    watches[0].next({ status: 'found', operation: run({ status: 'Running', stagedCount: 1, images: [picture()] }) });
    expect(tracker.connection()).toBe('live');

    watches[0].next({ status: 'unavailable' });

    expect(tracker.connection()).toBe('degraded');
    // The picture a creator is looking at stays on screen. One failed poll must not blank it.
    expect(tracker.detail()?.images.length).toBe(1);
    expect(tracker.phase().kind).toBe('watching');
  });

  it('reports a run that is gone, and one it may not read, differently', async () => {
    await tracker.submit(() => Promise.resolve(accepted()));
    watches[0].next({ status: 'not_found' });
    expect(tracker.phase().kind).toBe('gone');

    await tracker.submit(() => Promise.resolve(accepted()));
    watches[1].next({ status: 'forbidden' });
    expect(tracker.phase().kind).toBe('forbidden');
  });

  it('maps each refusal to its own phase, because the remedies differ', async () => {
    await tracker.submit(() =>
      Promise.resolve<GeneratedImageRequestOutcome>({ status: 'forbidden' }),
    );
    expect(tracker.phase().kind).toBe('forbidden');

    await tracker.submit(() => Promise.resolve<GeneratedImageRequestOutcome>({ status: 'rate_limited' }));
    expect(tracker.phase().kind).toBe('rate_limited');

    await tracker.submit(() =>
      Promise.resolve<GeneratedImageRequestOutcome>({
        status: 'refused',
        message: 'Ask for between 1 and 4 images.',
        fieldErrors: { VariantCount: ['Ask for between 1 and 4 images.'] },
      }),
    );
    const phase = tracker.phase();
    expect(phase.kind).toBe('refused');
    if (phase.kind === 'refused') expect(phase.message).toBe('Ask for between 1 and 4 images.');
  });

  it('reports a missing idempotency key as an outage, since there is nothing a creator can correct', async () => {
    await tracker.submit(() => Promise.resolve<GeneratedImageRequestOutcome>({ status: 'key_required' }));

    expect(tracker.phase().kind).toBe('unavailable');
    expect(watched).withContext('nothing to watch').toEqual([]);
  });

  it('watches nothing and keeps no id when the ask was refused', async () => {
    const operationId = await tracker.submit(() =>
      Promise.resolve<GeneratedImageRequestOutcome>({ status: 'forbidden' }),
    );

    expect(operationId).toBeNull();
    expect(tracker.operationId()).toBeNull();
  });

  it('picks a run back up by its id, which is what a refresh does', () => {
    tracker.resume('op-7');

    expect(tracker.operationId()).toBe('op-7');
    expect(tracker.phase().kind).toBe('watching');
    expect(watched).toEqual(['op-7']);
  });

  it('re-reads without blanking the pictures already on screen', async () => {
    await tracker.submit(() => Promise.resolve(accepted()));
    watches[0].next({
      status: 'found',
      operation: run({ status: 'Succeeded', stagedCount: 1, images: [picture()] }),
    });

    tracker.reread();

    expect(watched).toEqual(['op-1', 'op-1']);
    expect(tracker.phase().kind).toBe('watching');
    expect(tracker.detail()?.images.length).withContext('the sheet stays up while we ask again').toBe(1);
  });

  it('re-reads nothing when there is no run, rather than asking for an empty id', () => {
    tracker.reread();

    expect(watched).toEqual([]);
    expect(tracker.phase().kind).toBe('idle');
  });

  it('stops checking without claiming to have cancelled anything', async () => {
    await tracker.submit(() => Promise.resolve(accepted()));
    const subject = watches[0];

    tracker.stopChecking();

    expect(subject.observed).withContext('polling stopped').toBeFalse();
    expect(tracker.stopped()).toBeTrue();
    // The id is kept: the generation is still running server-side and can be picked back up.
    expect(tracker.operationId()).toBe('op-1');
  });

  it('never lets an abandoned answer land on the run the creator is looking at', async () => {
    await tracker.submit(() => Promise.resolve(accepted('op-first')));
    const first = watches[0];

    await tracker.submit(() => Promise.resolve(accepted('op-second')));

    expect(first.observed).toBeFalse();

    first.next({ status: 'found', operation: run({ id: 'op-first', status: 'Succeeded', images: [picture()] }) });

    expect(tracker.operationId()).toBe('op-second');
    expect(tracker.phase().kind).withContext('the first run’s answer is dropped').toBe('watching');
    expect(tracker.detail()).toBeNull();
  });

  it('is busy only while the creator is waiting on something', async () => {
    expect(tracker.busy()).toBeFalse();

    await tracker.submit(() => Promise.resolve(accepted()));
    expect(tracker.busy()).toBeTrue();

    watches[0].next({ status: 'found', operation: run({ status: 'Succeeded', images: [picture()] }) });
    expect(tracker.busy()).toBeFalse();
  });

  it('abandons an ask that is still in flight when it is destroyed, so no poll outlives the screen', async () => {
    let answer: (outcome: GeneratedImageRequestOutcome) => void = () => undefined;
    const pending = tracker.submit(() => new Promise<GeneratedImageRequestOutcome>((resolve) => (answer = resolve)));

    tracker.destroy();
    answer(accepted());

    expect(await pending).withContext('nothing to hand back').toBeNull();
    expect(watched).withContext('no poll was started').toEqual([]);
  });

  it('starts nothing once it has been destroyed, whatever a late continuation asks of it', async () => {
    await tracker.submit(() => Promise.resolve(accepted()));
    const before = watched.length;

    tracker.destroy();
    tracker.reread();
    tracker.resume('op-1');
    let asked = false;
    const again = await tracker.submit(() => {
      asked = true;

      return Promise.resolve(accepted());
    });

    expect(again).toBeNull();
    expect(asked).withContext('no request was made').toBeFalse();
    expect(watched.length).withContext('no poll was started').toBe(before);
  });

  it('forgets everything on reset', async () => {
    await tracker.submit(() => Promise.resolve(accepted()));

    tracker.reset();

    expect(tracker.phase().kind).toBe('idle');
    expect(tracker.operationId()).toBeNull();
    expect(watches[0].observed).toBeFalse();
  });
});
