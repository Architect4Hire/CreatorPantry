import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { CpStatusPillComponent, CpStatusPillTone } from '@creator-pantry/ui';

import { AiFailureCategory, AiOperationStatus } from '../../models/ai-proposal.models';

/** Whether the panel's own polling is currently reaching the server. */
export type AiStatusConnection = 'live' | 'degraded';

/**
 * What each state is called to a creator.
 *
 * None of these words is the server's enum member, and that is the point: "Requested" is a lifecycle term,
 * and a creator wants to know whether anything is happening yet. `PartiallyAccepted` reads as "Some changes
 * accepted" rather than a status name, because what a creator remembers is the decision they made.
 */
const STATUS_LABELS: Readonly<Record<AiOperationStatus, string>> = {
  Requested: 'Queued',
  Running: 'Working',
  Proposed: 'Ready for review',
  Accepted: 'Accepted',
  PartiallyAccepted: 'Some changes accepted',
  Rejected: 'Declined',
  Failed: "Didn't finish",
  Expired: 'Timed out',
};

const STATUS_TONES: Readonly<Record<AiOperationStatus, CpStatusPillTone>> = {
  Requested: 'progress',
  Running: 'progress',
  // Warning rather than success: something is waiting for the creator, and a green tick beside content that
  // is not saved yet would say the opposite of what the panel says everywhere else.
  Proposed: 'warning',
  Accepted: 'success',
  PartiallyAccepted: 'success',
  Rejected: 'neutral',
  Failed: 'error',
  Expired: 'stale',
};

/**
 * What each state means, in a sentence, with no lifecycle, prompt, template or storage vocabulary.
 *
 * `Proposed` says "nothing has been saved" because that is the fact the whole panel rests on, and it is
 * repeated here rather than left to the badge beside it: one of them may be the only one a creator reads.
 */
const STATUS_DETAIL: Readonly<Record<AiOperationStatus, string>> = {
  Requested: 'Your request is in the queue. This page updates on its own.',
  Running: 'Working on your request now. This page updates on its own.',
  Proposed: 'Suggestions are ready for you to review. Nothing has been saved to your recipe.',
  Accepted: 'You accepted every suggestion.',
  PartiallyAccepted: 'You accepted some of the suggestions and left the rest.',
  Rejected: 'You declined these suggestions. Nothing was changed.',
  Failed: 'This request stopped before it produced anything. Your recipe was not changed.',
  Expired: 'Too much time passed, so this request was closed. Your recipe was not changed.',
};

/**
 * Why a request stopped, in terms a creator can act on.
 *
 * Deliberately vague about the provider in the two cases where being specific would mean naming
 * infrastructure a creator has no relationship with: `Provider` and `LeaseAbandoned` both read as "something
 * on our side", because "the model endpoint was unreachable" and "every worker died mid-attempt" ask the same
 * thing of a creator, which is to try again.
 *
 * `Unspecified` has a line because a stored row can carry it even though the check constraint refuses it — and
 * a panel that rendered nothing for an unrecognised category would silently drop the only explanation there is.
 */
const FAILURE_DETAIL: Readonly<Record<AiFailureCategory, string>> = {
  Unspecified: 'No reason was recorded for this one.',
  Validation: 'This request could not be run as asked.',
  Quota: "This workspace has used up its allowance for now. It'll be available again later.",
  TemplateUnavailable: 'A piece of setup this request needs is missing, so nothing was sent.',
  Provider: 'Something on our side did not respond. Asking again usually works.',
  RateLimited: 'Too many requests at once. Waiting a moment and asking again usually works.',
  Timeout: 'This took too long to answer. Asking again usually works.',
  OutputSchemaInvalid: "The answer came back in a shape we couldn't use, so none of it was kept.",
  DomainInvalid: "The answer broke one of your recipe's own rules, so none of it was kept.",
  SafetyBlocked: 'This request was stopped on safety grounds and will not be retried automatically.',
  Cancelled: 'This one was cancelled.',
  LeaseAbandoned: 'Something on our side kept dropping this request. Asking again usually works.',
};

/**
 * Whether asking again is worth offering.
 *
 * The distinction `AiFailureCategory` itself draws: a timeout is worth another attempt, and output that
 * failed its schema, a domain rule or a safety check is not — retrying those spends a creator's provider
 * budget to arrive at the same answer. `SafetyBlocked` is never retried, which ai.md states outright.
 */
const RETRYABLE_FAILURES: ReadonlySet<AiFailureCategory> = new Set<AiFailureCategory>([
  'Provider',
  'RateLimited',
  'Timeout',
  'Cancelled',
  'LeaseAbandoned',
]);

/** Whether a creator could sensibly ask again after this outcome. `Expired` qualifies: nobody acted in time. */
export function isRetryableAiOutcome(status: AiOperationStatus, failure: AiFailureCategory | null): boolean {
  if (status === 'Expired') return true;
  if (status !== 'Failed') return false;
  return failure !== null && RETRYABLE_FAILURES.has(failure);
}

/**
 * Where one AI request has got to, stated in words rather than implied by colour.
 *
 * Presentation only: no polling, no requests, no actions, and no opinion about what should happen next. It
 * renders what the server said about an operation and announces changes to it, so that a creator who is not
 * watching the screen is told when generated content arrives or a request stops.
 *
 * The tone of the pill carries no information the text does not (WCAG 2.2 AA, 1.4.1) — every state names
 * itself, and the pill's glyph is decorative.
 */
@Component({
  selector: 'cp-ai-operation-status',
  standalone: true,
  imports: [CpStatusPillComponent],
  templateUrl: './ai-operation-status.component.html',
  styleUrl: './ai-operation-status.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AiOperationStatusComponent {
  readonly status = input.required<AiOperationStatus>();

  /** Present only when the status is `Failed`; the server requires one there and none anywhere else. */
  readonly failureCategory = input<AiFailureCategory | null>(null);

  readonly requestedAt = input.required<string>();
  readonly statusChangedAt = input.required<string>();

  /**
   * Whether this is live. `degraded` means the last poll did not reach the server, so what is shown is the
   * last known state rather than the current one — which the panel must say, not imply by going quiet.
   */
  readonly connection = input<AiStatusConnection>('live');

  readonly label = computed(() => STATUS_LABELS[this.status()]);
  readonly tone = computed(() => STATUS_TONES[this.status()]);
  readonly detail = computed(() => STATUS_DETAIL[this.status()]);

  readonly failureDetail = computed(() => {
    const failure = this.failureCategory();
    return this.status() === 'Failed' && failure !== null ? FAILURE_DETAIL[failure] : null;
  });

  /** A failure is announced assertively; every other transition is polite. */
  readonly isFailure = computed(() => this.status() === 'Failed');

  readonly requestedAtLabel = computed(() => this.timeOf(this.requestedAt()));
  readonly changedAtLabel = computed(() => this.timeOf(this.statusChangedAt()));

  /**
   * One sentence for the live region, rebuilt whenever anything it names changes.
   *
   * The whole state in one string rather than several elements, so a screen reader hears one announcement
   * instead of three fragments arriving in whatever order change detection produced them.
   */
  readonly announcement = computed(() => {
    const parts = [this.label(), this.detail()];
    const failure = this.failureDetail();
    if (failure !== null) parts.push(failure);
    if (this.connection() === 'degraded') {
      parts.push(`This is the last update we could get, from ${this.changedAtLabel()}.`);
    }
    return parts.join(' ');
  });

  /**
   * A local wall-clock time, or the raw value if it cannot be read as one.
   *
   * Time only, not a date: everything this component shows happened within one sitting, and a full date beside
   * "Working" would be noise. A value that does not parse is shown as it arrived rather than as "Invalid Date".
   */
  private timeOf(value: string): string {
    const parsed = new Date(value);
    return Number.isNaN(parsed.getTime())
      ? value
      : parsed.toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit' });
  }
}
