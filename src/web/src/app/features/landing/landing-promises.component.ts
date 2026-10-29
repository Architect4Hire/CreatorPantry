import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { CpBadgeComponent, CpCardComponent, CpCardGridComponent } from '@creator-pantry/ui';

import { CreditPack } from '../../models/plan-catalogue.models';
import { PlanCatalogueService } from '../../services/plan-catalogue.service';
import { formatMoney } from './landing-money';

/**
 * The label a promise carries while the product cannot keep it yet. Status is these words, never a tone:
 * `cp-badge` paints without a glyph, and a reader who cannot tell the colours apart would otherwise read a
 * promise as kept.
 */
export const COMING_WITH_BILLING = 'Coming with billing';

/** One promise, and what backs it today. */
export interface BillingPromise {
  readonly heading: string;
  readonly body: string;
  /** Absent where the product already behaves this way. */
  readonly comingLabel?: string;
}

/**
 * The three promises about what a creator will and will not be charged (LP.2 section 9, LND-004).
 *
 * Only the first describes billing that is not built, and it says so on its own face rather than in a
 * footnote. The other two are how the product behaves today: an allowance at nought refuses the work before
 * it starts, and an account running low is told once and offered a pack (AP.6.4).
 */
export const BILLING_PROMISES: readonly BillingPromise[] = [
  {
    heading: 'Prepaid, and that is all',
    body: 'You pay for credits before you use them. Nothing is billed to you afterwards.',
    comingLabel: COMING_WITH_BILLING,
  },
  {
    heading: 'At zero, AI waits',
    body: 'When your credits run out, AI requests stop before they start. Your recipes, pictures and drafts stay yours to read, edit and download.',
  },
  {
    // B-59: there is no automatic top-up. Running low brings a notice offering a pack, which the creator buys.
    // Unlabelled since AP.6.4 built the notice: the product does this now, and the pack the notice offers is a
    // Checkout the creator completes.
    heading: 'Running low? You hear first',
    body: 'When your credits run low, we tell you and offer a pack. Nothing is ever bought for you.',
  },
];

/**
 * "No surprise bills" (LP.8): what a creator will be charged, and the packs that sit on top of a plan.
 *
 * Every figure is the catalogue's, as everywhere else in this part of the page, and the section does not render
 * while the catalogue publishes nothing. A pack states how long it lasts only where the catalogue says so —
 * how long a pack's credits keep is still proposed rather than decided, and an unanswered question is not a
 * promise to make on a marketing page.
 */
@Component({
  selector: 'cp-landing-promises',
  standalone: true,
  imports: [CpCardComponent, CpCardGridComponent, CpBadgeComponent],
  templateUrl: './landing-promises.component.html',
  styleUrl: './landing-promises.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LandingPromisesComponent {
  private readonly catalogueService = inject(PlanCatalogueService);

  protected readonly catalogue = toSignal(this.catalogueService.catalogue(), {
    initialValue: PlanCatalogueService.EMPTY,
  });

  protected readonly heading = 'No surprise bills. Ever.';
  protected readonly promises = BILLING_PROMISES;
  protected readonly packsHeading = 'Need more in a busy month?';
  /** A decided promise, not a hedge: a pack is credits already paid for (owner, 2026-10-24). */
  protected readonly packsNote = 'A pack is credits you have bought, so they keep until you spend them. They never expire.';
  protected readonly comingWithBilling = COMING_WITH_BILLING;

  protected readonly packs = computed(() => this.catalogue().packs);
  protected readonly renders = computed(() => this.catalogue().plans.length > 0);

  protected readonly currency = computed(() => this.catalogue().currency);

  /** A pack in one line: what it costs and what it buys. How long it keeps is said once, under the list. */
  protected describe(pack: CreditPack): string {
    return `${pack.credits.toLocaleString()} credits for ${formatMoney(this.currency(), pack.price)}, used after your plan’s credits.`;
  }
}
