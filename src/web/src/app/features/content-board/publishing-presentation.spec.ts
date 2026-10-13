import { PublishingRecord } from '../../models/publishing.models';
import { publishingRecordView, publishingSummary } from './publishing-presentation';

function record(over: Partial<PublishingRecord> = {}): PublishingRecord {
  return {
    id: 'rec-1',
    contentCardId: 'card-1',
    publishingProfileId: 'prof-1',
    platform: 'Instagram',
    status: 'Scheduled',
    text: 'Soda bread, out of the oven at last.',
    scheduledAt: '2026-10-16T13:30:00Z',
    scheduleTimeZoneId: 'America/Chicago',
    scheduledLocal: '2026-10-16T08:30:00',
    outcomeUncertain: false,
    failureCategory: null,
    failureSummary: null,
    submittedAt: '2026-10-13T12:00:00Z',
    completedAt: null,
    submitAttempts: 1,
    confirmedAt: '2026-10-13T11:59:00Z',
    concurrencyToken: 'AAAAAAAAB9E=',
    ...over,
  };
}

describe('publishing presentation', () => {
  it('never rounds an uncertain submit to success or failure, and says not to schedule again', () => {
    const view = publishingRecordView(record({ status: 'Submitting', outcomeUncertain: true }));

    expect(view.tone).toBe('warning');
    expect(view.label).toBe('Outcome unknown');
    expect(view.explanation).toContain('did not get an answer');
    expect(view.guidance).toContain('Do not schedule it again');

    // Cancelling is not offered: without an answer there is no post this page can name.
    expect(view.cancellable).toBeFalse();
  });

  it('tells a creator an unconfirmed cancellation is safe to repeat', () => {
    const view = publishingRecordView(record({ status: 'Cancelling', outcomeUncertain: true }));

    expect(view.tone).toBe('warning');
    expect(view.guidance).toContain('safe');
    expect(view.cancellable).toBeTrue();
  });

  it('says what a failure was in the server’s words and what to do next', () => {
    const view = publishingRecordView(
      record({ status: 'Failed', failureSummary: 'The publishing provider would not accept this post as written.' }),
    );

    expect(view.tone).toBe('error');
    expect(view.explanation).toBe('The publishing provider would not accept this post as written.');
    expect(view.guidance).toContain('schedule it again');
    expect(view.cancellable).toBeFalse();
  });

  it('offers no cancellation for anything already finished', () => {
    for (const status of ['Published', 'Cancelled', 'Failed'] as const) {
      expect(publishingRecordView(record({ status })).cancellable).toBeFalse();
    }
  });

  it('says a state it does not recognise is unrecognised rather than guessing', () => {
    const view = publishingRecordView(record({ status: 'Unrecognised' }));

    expect(view.label).toBe('Unrecognised');
    expect(view.explanation).toContain('does not recognise');
    expect(view.cancellable).toBeFalse();
  });

  it('summarises a card by what most needs attention, not by what is newest', () => {
    expect(publishingSummary([])).toEqual({ label: 'Not scheduled', tone: 'neutral' });

    expect(publishingSummary([record({ status: 'Published' }), record({ id: 'rec-2', status: 'Failed' })])).toEqual({
      label: 'Not published',
      tone: 'error',
    });

    // An unknown outcome outranks a failure: it is the one a creator must not act on.
    expect(
      publishingSummary([
        record({ status: 'Failed' }),
        record({ id: 'rec-2', status: 'Submitting', outcomeUncertain: true }),
      ]).tone,
    ).toBe('warning');
  });
});
