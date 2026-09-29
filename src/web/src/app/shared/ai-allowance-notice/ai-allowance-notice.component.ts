import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { CpStatusPillComponent } from '@creator-pantry/ui';

import { AiQuotaRefusal } from '../../models/ai-quota.models';
import { AiAllowanceState } from '../../services/ai-usage.service';
import { formatAllowanceAmount, formatAllowanceReset } from '../allowance-text';

/** What the notice is saying, or nothing at all. */
type NoticeKind = 'nearly-spent' | 'exhausted' | 'suspended';

interface Notice {
  readonly kind: NoticeKind;
  /** True once a request has actually been refused, which changes how it is announced. */
  readonly refused: boolean;
  readonly heading: string;
  readonly detail: string;
}

/**
 * What a creator is told about their AI allowance on a screen that spends it (USAGE-010).
 *
 * <strong>One component, six call sites, one set of words.</strong> Every AI request screen shows the same
 * two things — a warning before the limit and a refusal at it — and six copies of that copy would have drifted
 * the first time one of them was reworded.
 *
 * <strong>It says nothing when it does not know.</strong> A healthy allowance, a first read still in flight, a
 * degraded read and a failed one all render nothing: a warning surface that spoke on ignorance would train a
 * creator to ignore it.
 *
 * <strong>No jargon.</strong> Credits and a date. No provider, no deployment, no model name, no token count —
 * the figures behind this carry none, and EASE-004 asks that none appear.
 */
@Component({
  selector: 'cp-ai-allowance-notice',
  standalone: true,
  imports: [CpStatusPillComponent],
  templateUrl: './ai-allowance-notice.component.html',
  styleUrl: './ai-allowance-notice.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AiAllowanceNoticeComponent {
  /** The account's allowance, from `AiUsageService.allowance()`. */
  readonly allowance = input.required<AiAllowanceState>();

  /**
   * A refusal the server has already returned for a submitted request, if any.
   *
   * It outranks the pre-flight warning: the creator has asked and been told no, and that is the more specific
   * thing to say.
   */
  readonly refusal = input<AiQuotaRefusal | null>(null);

  protected readonly notice = computed<Notice | null>(() => {
    const refusal = this.refusal();

    if (refusal !== null) {
      if (refusal.status === 'account_suspended') return { ...SUSPENDED, refused: true };

      const needed = formatAllowanceAmount(refusal.required, refusal.unit);
      const left = formatAllowanceAmount(refusal.remaining, refusal.unit);

      return {
        kind: 'exhausted',
        refused: true,
        heading: 'Your AI allowance is spent',

        // Figures only when the unit is one worth quoting. Otherwise the same refusal, described: a creator
        // is owed an explanation, not a token count wearing a different noun.
        detail:
          (needed !== null && left !== null
            ? `That request needed ${needed} and there ${refusal.remaining === 1 ? 'is' : 'are'} ${left} left. `
            : 'That request needed more than you have left. ') +
          `${resetSentence(refusal.resetsAt, null)} Nothing you have typed has been lost.`,
      };
    }

    const allowance = this.allowance();

    switch (allowance.kind) {
      // Nothing read, a healthy balance, or figures we are no longer sure of. None of those is worth
      // interrupting somebody for, and the last two must not read as a refusal.
      case 'unknown':
      case 'degraded':
      case 'healthy':
        return null;

      case 'nearly-spent': {
        const left = formatAllowanceAmount(allowance.period.remaining, allowance.period.unit);
        const total = formatAllowanceAmount(allowance.period.allowance, allowance.period.unit);

        return {
          kind: 'nearly-spent',
          refused: false,
          heading: 'Your AI allowance is nearly spent',
          detail:
            (left !== null && total !== null ? `${left} left of ${total}. ` : 'Not much left this period. ') +
            resetSentence(allowance.period.resetsAt, allowance.period.timeZoneId),
        };
      }

      case 'exhausted':
        return {
          kind: 'exhausted',
          refused: false,
          heading: 'Your AI allowance is spent',
          detail:
            'You can keep writing and editing recipes as usual. ' +
            resetSentence(allowance.period.resetsAt, allowance.period.timeZoneId),
        };

      case 'suspended':
        return { ...SUSPENDED, refused: false };
    }
  });

  protected readonly tone = computed(() => (this.notice()?.kind === 'nearly-spent' ? 'warning' : 'error'));

  protected readonly pillLabel = computed(() => {
    switch (this.notice()?.kind) {
      case 'nearly-spent':
        return 'Nearly spent';
      case 'suspended':
        return 'Switched off';
      default:
        return 'Spent';
    }
  });
}

/**
 * Suspension says something different from exhaustion, and must.
 *
 * An allowance comes back on its own at a known instant; a suspension does not come back at all. Offering
 * "it'll be available again later" for both would leave a suspended creator waiting for something that is
 * never going to happen — the distinction `AiOperationStatusComponent` already draws.
 */
const SUSPENDED = {
  kind: 'suspended',
  heading: 'AI assistance is switched off for your account',
  detail: 'Get in touch and we can look into it. Everything else in CreatorPantry works as usual.',
} as const satisfies Omit<Notice, 'refused'>;

function resetSentence(resetsAt: string, timeZoneId: string | null): string {
  const when = formatAllowanceReset(resetsAt, timeZoneId);

  return when === null ? 'It comes back when the period resets.' : `It comes back on ${when}.`;
}
