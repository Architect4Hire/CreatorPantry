// How the images step talks about a run and the pictures in it. Words and tones only — no requests, no state.
//
// Shared by the grid and the lightbox so a picture cannot be called one thing in a tile and another thing when
// it is opened larger.

import { CpStatusPillTone } from '@creator-pantry/ui';

import { GeneratedImageOperationStatus, GeneratedImageStatus } from '../../models/generated-image.models';

/**
 * What each picture state is called to a creator.
 *
 * None of these is the server's enum member. `Staged` reads as **Ready** because that is what it means to the
 * person looking at it, and `Kept` reads as **Filed in your library** because that status only ever arrives
 * from the library commit — which this journey does not make, so a creator who sees it got there another way
 * and the word has to tell them so rather than echo the mark they made here.
 */
export const STAGED_IMAGE_STATUS_LABELS: Readonly<Record<GeneratedImageStatus, string>> = {
  Unspecified: 'Unknown',
  Staged: 'Ready',
  Kept: 'Filed in your library',
  Rejected: 'Declined',
  Expired: 'No longer available',
};

/** The tone is the quieter half of something the label already said; it never carries a state on its own. */
export const STAGED_IMAGE_STATUS_TONES: Readonly<Record<GeneratedImageStatus, CpStatusPillTone>> = {
  Unspecified: 'neutral',
  Staged: 'success',
  Kept: 'success',
  Rejected: 'neutral',
  Expired: 'stale',
};

/** What each run state is called. `PartiallySucceeded` names the shortfall, because that is the whole news. */
export const GENERATED_IMAGE_RUN_LABELS: Readonly<Record<GeneratedImageOperationStatus, string>> = {
  Unspecified: 'Unknown',
  Requested: 'Queued',
  Running: 'Making your pictures',
  Succeeded: 'Ready to choose',
  PartiallySucceeded: 'Some of them arrived',
  Failed: "Didn't finish",
  Cancelled: 'Stopped',
};

export const GENERATED_IMAGE_RUN_TONES: Readonly<Record<GeneratedImageOperationStatus, CpStatusPillTone>> = {
  Unspecified: 'neutral',
  Requested: 'progress',
  Running: 'progress',
  // Warning rather than success: pictures are waiting on a decision, and a green tick beside something not
  // yet chosen would say the opposite of what the rest of the step says.
  Succeeded: 'warning',
  PartiallySucceeded: 'warning',
  Failed: 'error',
  Cancelled: 'neutral',
};

/** What each run state means, in a sentence, with no queue, lease, provider or storage vocabulary. */
export const GENERATED_IMAGE_RUN_DETAIL: Readonly<Record<GeneratedImageOperationStatus, string>> = {
  Unspecified: 'We are not sure where this one got to. Asking again is the thing to try.',
  Requested: 'Your request is in the queue. This page updates on its own.',
  Running: 'Making your pictures now. They appear here as they arrive.',
  Succeeded: 'All your pictures arrived. Keep the ones worth using.',
  PartiallySucceeded: 'Not all of them arrived, but what did is below. You can keep from these or try again.',
  Failed: 'This stopped before it made anything.',
  Cancelled: 'This one was stopped.',
};

/** A byte count as a creator reads it. Decimal units, which is what a file manager shows. */
export function fileSizeText(sizeBytes: number): string {
  if (sizeBytes < 1000) return `${sizeBytes} bytes`;
  if (sizeBytes < 1000 * 1000) return `${Math.round(sizeBytes / 1000)} KB`;

  return `${(sizeBytes / (1000 * 1000)).toFixed(1)} MB`;
}

/** `image/png` as a creator reads it. Falls back to the media type itself for anything unexpected. */
export function imageFormatText(mediaType: string): string {
  const subtype = mediaType.startsWith('image/') ? mediaType.slice('image/'.length) : mediaType;

  return subtype.toUpperCase();
}

/**
 * When the staged pictures stop being available, as a date and time.
 *
 * The **soonest** deadline in the run, because that is the one that bites first, and stating a later one would
 * promise time the earliest picture does not have. Empty when nothing has a readable deadline, so the sentence
 * that uses it can be left out rather than printed around a blank.
 */
export function retentionDeadlineText(images: readonly { readonly retentionExpiresAt: string }[]): string {
  const soonest = images
    .map((image) => new Date(image.retentionExpiresAt).getTime())
    .filter((time) => !Number.isNaN(time))
    .sort((left, right) => left - right)[0];

  if (soonest === undefined) return '';

  return new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' }).format(
    new Date(soonest),
  );
}
