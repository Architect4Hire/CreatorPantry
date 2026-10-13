import { PublishingProfile, PublishingRecord, PublishingStatusValue } from '../../models/publishing.models';

/** The pill tones `cp-status-pill` gives, each with a glyph of its own as well as a fill. */
export type PublishingTone = 'neutral' | 'progress' | 'success' | 'warning' | 'error';

/** One publication as the page says it: a word, a tone, a sentence, and what to do next. */
export interface PublishingRecordView {
  readonly record: PublishingRecord;
  readonly label: string;
  readonly tone: PublishingTone;
  /** What is true about this publication, in plain words. */
  readonly explanation: string;
  /** What the creator can do about it, when there is something. */
  readonly guidance: string | null;
  /** Whether a cancellation is worth offering. The server is still the authority on whether it is allowed. */
  readonly cancellable: boolean;
}

/**
 * Where one publication stands, in words.
 *
 * **An uncertain outcome is never rounded.** It gets its own warning tone and its own sentence, and it is the
 * one state whose guidance tells the creator *not* to act: scheduling again could publish the same post
 * twice, and the server is already finding out what happened.
 *
 * **A state this build does not recognise is said to be unrecognised** rather than guessed at or hidden. The
 * status is an open set — the server may learn a new one before this screen does.
 */
export function publishingRecordView(record: PublishingRecord): PublishingRecordView {
  if (record.outcomeUncertain) {
    return {
      record,
      label: 'Outcome unknown',
      tone: 'warning',
      explanation:
        record.status === 'Cancelling'
          ? 'We asked the provider to remove this and did not get an answer, so we do not yet know whether it is gone.'
          : 'We asked the provider to publish this and did not get an answer, so we do not yet know whether it went out.',
      guidance:
        record.status === 'Cancelling'
          ? 'Cancelling again is safe — it cannot remove two posts.'
          : 'Do not schedule it again yet. CreatorPantry is checking with the provider, and scheduling again could publish it twice.',
      cancellable: record.status === 'Cancelling',
    };
  }

  switch (record.status) {
    case 'Scheduled':
      return {
        record,
        label: 'Scheduled',
        tone: 'success',
        explanation: 'The provider has this and will publish it at the time below.',
        guidance: null,
        cancellable: true,
      };
    case 'Pending':
      return {
        record,
        label: 'Waiting to send',
        tone: 'progress',
        explanation: 'Not sent yet — the provider was busy or could not be reached. Nothing has been published.',
        guidance: 'CreatorPantry will keep trying. There is nothing you need to do.',
        cancellable: true,
      };
    case 'Submitting':
      return {
        record,
        label: 'Sending',
        tone: 'progress',
        explanation: 'This is being handed to the provider now.',
        guidance: null,
        cancellable: false,
      };
    case 'Published':
      return {
        record,
        label: 'Published',
        tone: 'success',
        explanation: 'The provider reports that this went out.',
        guidance: null,
        cancellable: false,
      };
    case 'Cancelling':
      return {
        record,
        label: 'Cancelling',
        tone: 'progress',
        explanation: 'We have asked the provider to remove this post.',
        guidance: null,
        cancellable: true,
      };
    case 'Cancelled':
      return {
        record,
        label: 'Cancelled',
        tone: 'neutral',
        explanation: 'This will not go out.',
        guidance: null,
        cancellable: false,
      };
    case 'Failed':
      return {
        record,
        label: 'Not published',
        tone: 'error',
        explanation: record.failureSummary ?? 'The provider would not take this post. Nothing went out.',
        guidance: 'Fix what the provider objected to, then schedule it again.',
        cancellable: false,
      };
    case 'NeedsAttention':
      return {
        record,
        label: 'Needs attention',
        tone: 'warning',
        explanation:
          record.failureSummary ?? 'CreatorPantry could not settle this with the provider after several tries.',
        guidance: 'Check this post at the provider before scheduling it again here.',
        cancellable: true,
      };
    default:
      return {
        record,
        label: 'Unrecognised',
        tone: 'neutral',
        explanation: 'This publication is in a state this page does not recognise yet.',
        guidance: 'Reload the page. If it stays like this, it is worth reporting.',
        cancellable: false,
      };
  }
}

/** What the card's own badge says about publishing, from its publications and nothing else. */
export function publishingSummary(records: readonly PublishingRecord[]): { label: string; tone: PublishingTone } {
  if (records.length === 0) return { label: 'Not scheduled', tone: 'neutral' };

  const views = records.map(publishingRecordView);

  // The most important thing first: something nobody can account for, then something that needs a person,
  // then a failure, then what is still coming, then what went out.
  const order: readonly PublishingTone[] = ['warning', 'error', 'progress', 'success', 'neutral'];

  for (const tone of order) {
    const found = views.find((view) => view.tone === tone);

    if (found) return { label: found.label, tone: found.tone };
  }

  return { label: 'Not scheduled', tone: 'neutral' };
}

/** Why a profile cannot be posted to, or null when it can. */
export function profileUnavailableReason(profile: PublishingProfile): string | null {
  switch (profile.status) {
    case 'Ready':
      return null;
    case 'NeedsReconnecting':
      return 'needs reconnecting at the provider';
    case 'Locked':
      return 'locked by the provider';
    default:
      return 'in a state this page does not recognise';
  }
}

/** What a status means for whether anything may be scheduled at all. */
export const PUBLISHING_CONNECTION_NOTES = {
  NotConfigured: 'No publishing provider is connected for this workspace, so nothing can be scheduled from here yet.',
  CredentialRejected:
    'The publishing provider refused this deployment’s credential. The profiles below are from the last successful read.',
  RateLimited: 'The publishing provider is busy. The profiles below are from the last successful read.',
  Unavailable: 'The publishing provider could not be reached. The profiles below are from the last successful read.',
  Unrecognised: 'The publishing provider answered in a way this page does not recognise.',
} as const satisfies Record<Exclude<PublishingStatusValue | string, never> & string, string> | Record<string, string>;
