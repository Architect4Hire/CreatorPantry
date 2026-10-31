import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { RouterLink } from '@angular/router';
import { CpButtonComponent, CpNoticeComponent } from '@creator-pantry/ui';

import { AiLowCreditNotice } from '../../services/ai-usage.service';
import { formatAllowanceAmount, formatAllowanceReset } from '../allowance-text';
import { PACK_PURCHASE_LINK } from '../pack-purchase-link';

/**
 * "Your AI credits are running low", and the pack that answers it (PRICE-011 as amended, B-59).
 *
 * <strong>Nothing is ever bought for the creator.</strong> The one action here is a link to where they can
 * buy a pack themselves; no card is charged, no purchase is started, and the notice has no power to spend
 * anything. That is the promise the landing page makes, kept by there being no other button.
 *
 * <strong>Said once a period, and the server decides when.</strong> Whether there is anything to say is
 * `AiUsageService.lowCredit`, which reads a mark admission left on the period. This component renders it and
 * works nothing out — two surfaces show it, and neither may come to its own view of the figures.
 *
 * <strong>A live region that is always here.</strong> The notice usually arrives after an AI request has been
 * accepted, part-way through somebody's work, so the region is in the page before it has anything in it
 * (`quiet`) rather than being inserted with text already in place, which a screen reader may never announce.
 */
@Component({
  selector: 'cp-low-credit-notice',
  standalone: true,
  imports: [CpNoticeComponent, CpButtonComponent, RouterLink],
  templateUrl: './low-credit-notice.component.html',
  styleUrl: './low-credit-notice.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LowCreditNoticeComponent {
  /** The notice to show, or null when there is nothing to say. */
  readonly notice = input.required<AiLowCreditNotice | null>();

  /**
   * Whether to offer the link to buy a pack.
   *
   * False on the page the link points at: sending somebody to the page they are reading is not an offer, it
   * is a loop. The notice still says what is left and still dismisses.
   */
  readonly offerPack = input(true);

  /** The creator put it away. The caller records it; this component only asks. */
  readonly dismissed = output<void>();

  protected readonly packLink = PACK_PURCHASE_LINK;

  protected readonly detail = computed(() => {
    const notice = this.notice();
    if (notice === null) return '';

    const left = formatAllowanceAmount(notice.spendable, notice.unit);

    return [
      left === null
        ? 'There is not much left to spend.'
        : `${left} left — your allowance and your packs together.`,
      resetSentence(notice),
      'A credit pack tops you up now, and pack credits don’t expire.',
    ].join(' ');
  });
}

/**
 * When the allowance comes back, or that it does not.
 *
 * A trial's credits are granted once (B-48), and a notice that promised them back on a date would be telling
 * somebody to wait for something that is not coming.
 */
function resetSentence(notice: AiLowCreditNotice): string {
  if (!notice.allowanceRenews) return 'These credits were granted once and don’t renew.';

  const when = formatAllowanceReset(notice.resetsAt, notice.timeZoneId);

  return when === null ? 'Your allowance comes back when the period resets.' : `Your allowance comes back on ${when}.`;
}
