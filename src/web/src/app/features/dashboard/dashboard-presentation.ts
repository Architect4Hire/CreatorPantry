import { PublishingStatusValue } from '../../models/publishing.models';
import { TestIssueSeverity } from '../../models/recipe-test-run.models';
import { WorkTaskStatus } from '../../models/work-task.models';

/** The tones `cp-status-pill` gives, each with a glyph of its own as well as a fill. */
export type DashboardTone = 'neutral' | 'progress' | 'success' | 'warning' | 'error';

export interface DashboardStatusWords {
  readonly label: string;
  readonly tone: DashboardTone;
}

/**
 * A publishing state in a word, with the same words and tones the card page's publishing controls use
 * (`publishingRecordView`), so a post reads the same on the dashboard as on its card.
 */
export const PUBLISHING_STATUS_WORDS: Readonly<Record<PublishingStatusValue, DashboardStatusWords>> = {
  Pending: { label: 'Waiting to send', tone: 'progress' },
  Submitting: { label: 'Sending', tone: 'progress' },
  Scheduled: { label: 'Scheduled', tone: 'success' },
  Published: { label: 'Published', tone: 'success' },
  Cancelling: { label: 'Cancelling', tone: 'progress' },
  Cancelled: { label: 'Cancelled', tone: 'neutral' },
  Failed: { label: 'Not published', tone: 'error' },
  NeedsAttention: { label: 'Needs attention', tone: 'warning' },
  // Not "state not recognised": a creator is not looking at a state machine. Open the post to see where it is.
  Unrecognised: { label: 'Open it to see', tone: 'neutral' },
};

/** A test issue's severity in a word, with a tone that is never the only signal: the word is always shown. */
export const SEVERITY_WORDS: Readonly<Record<TestIssueSeverity, DashboardStatusWords>> = {
  Blocking: { label: 'Blocking', tone: 'error' },
  Major: { label: 'Major', tone: 'warning' },
  Minor: { label: 'Minor', tone: 'neutral' },
};

/**
 * A task's own state in a word, for a dashboard row. Never the only signal: the word is always shown, and a
 * row's group already says whether it is late.
 */
export const TASK_STATUS_WORDS: Readonly<Record<WorkTaskStatus, DashboardStatusWords>> = {
  Backlog: { label: 'Not started', tone: 'neutral' },
  InProgress: { label: 'Under way', tone: 'progress' },
  Waiting: { label: 'Held up', tone: 'warning' },
  Done: { label: 'Done', tone: 'success' },
};

/**
 * Why a guided workflow needs a look, in ordinary words — the server's codes
 * (`WorkflowRunAttentionReasons`), said the way the workflow hub says them.
 */
const ATTENTION_REASONS: Readonly<Record<string, string>> = {
  idle: 'Nobody has touched it in over a week',
  newer_version: 'A newer version of this workflow has shipped since it started',
  not_yours_to_do: 'The step it is on needs someone with more access than you have',
  version_gone: 'The version it started from is no longer available, so its steps cannot be shown',
};

/**
 * A reason this build does not know is left out rather than shown as a code. If that leaves nothing to say, the
 * row still says something: a workflow listed as needing a look with no reason beside it reads as a page fault,
 * and the creator has no way on. The workflow hub answers the same way for an unrecognised reason.
 */
export function attentionWords(reasons: readonly string[]): readonly string[] {
  const words = reasons
    .map((reason) => ATTENTION_REASONS[reason])
    .filter((text): text is string => text !== undefined);

  return words.length > 0 ? words : ['We could not say why. Open it to see where it stopped.'];
}
