import { DOCUMENT } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { CpBadgeComponent, CpButtonComponent, CpCardComponent, CpCardGridComponent, CpChoiceGroupComponent, CpChoiceOption } from '@creator-pantry/ui';

import { Plan } from '../../models/plan-catalogue.models';
import { LandingCount } from './landing-count';
import { formatMoney } from './landing-money';
import { PlanCatalogueService } from '../../services/plan-catalogue.service';

/** How a visitor says they would pay. The prices either way are the catalogue's; this picks which to show. */
export type BillingPeriod = 'monthly' | 'annual';

export const BILLING_PERIODS: readonly CpChoiceOption[] = [
  { value: 'monthly', label: 'Monthly' },
  { value: 'annual', label: 'Billed yearly' },
];

/** The badge the catalogue's flagged plan carries. A recommendation, never a statistic (landing decision 2). */
export const RECOMMENDED_LABEL = 'Recommended for most food bloggers';

/** One column of the comparison table: a label the page owns, and the value each plan's own figures give it. */
export interface PlanComparisonColumn {
  readonly label: string;
  readonly valueOf: (plan: Plan) => string;
}

/**
 * The pricing section (LP.6): a card per plan, a monthly/yearly toggle, and a comparison table under them.
 *
 * **Nothing here knows a price.** Every figure — what a plan costs, what it includes, what a year saves —
 * arrives from the catalogue at runtime, so this bundle carries labels and layout only and the owner changes a
 * price in configuration. That is also why the section simply does not render when the catalogue is empty: a
 * deployment that publishes no plans, and a gateway that cannot be reached, are the same thing to a visitor.
 *
 * The figures a deployment currently publishes are proposals rather than decided prices, which is why the
 * catalogue is published in development alone until the owner settles them.
 */
@Component({
  selector: 'cp-landing-pricing',
  standalone: true,
  imports: [RouterLink, CpCardComponent, CpCardGridComponent, CpBadgeComponent, CpButtonComponent, CpChoiceGroupComponent],
  templateUrl: './landing-pricing.component.html',
  styleUrl: './landing-pricing.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LandingPricingComponent {
  private readonly catalogueService = inject(PlanCatalogueService);

  protected readonly catalogue = toSignal(this.catalogueService.catalogue(), {
    initialValue: PlanCatalogueService.EMPTY,
  });

  protected readonly heading = 'Plans that grow with what you make';
  protected readonly intro = 'Every plan includes the whole workspace. What changes is how much AI work a month includes.';
  protected readonly periods = BILLING_PERIODS;
  protected readonly recommendedLabel = RECOMMENDED_LABEL;

  /** What the question is called, in the page and as the radio group's own name. */
  protected readonly periodQuestion = 'How would you like to pay?';

  protected readonly period = signal<BillingPeriod>('annual');
  protected readonly comparisonOpen = signal(false);

  private readonly document = inject(DOCUMENT);

  /** The price counting between the two periods' figures. Cosmetic: see {@link LandingCount}. */
  private readonly count = new LandingCount(this.document.defaultView);

  constructor() {
    inject(DestroyRef).onDestroy(() => this.count.stop());
  }

  protected readonly plans = computed(() => this.catalogue().plans);

  /**
   * Whether this section is on the page at all.
   *
   * Public because the page above it has two decisions that depend on the answer and no second way to reach it:
   * whether its nav offers a Pricing anchor, and whether `/pricing` has anything to scroll to. Reading it here
   * rather than asking the catalogue again keeps one request, and keeps the section's own rule — no plans, no
   * section — the only place that decides.
   */
  readonly available = computed(() => this.plans().length > 0);

  /**
   * The table's columns — a plan to a row, so the table grows down the page as plans are added rather than
   * across it, and a phone reads it without a sideways scroll.
   *
   * Every value here is short on purpose. What a plan's credits buy is a sentence, and a column of sentences is
   * what made this table wider than the page; it is on each card already, where it is read rather than
   * compared. There is no quality column: the catalogue lists quality classes for AP.3.1, but a picture has
   * no quality to choose today, so no plan can differ by one.
   *
   * The labels are the page's; every value is the plan's own, so a figure cannot drift from the card above it
   * and no number is written into this file.
   */
  protected readonly comparisonColumns: readonly PlanComparisonColumn[] = [
    { label: 'Price', valueOf: (plan) => this.formatMoney(this.priceOf(plan)) },
    { label: 'Credits a month', valueOf: (plan) => this.formatCredits(plan.credits) },
    {
      label: 'Pictures a day',
      valueOf: (plan) =>
        plan.trialImageCap === null
          ? plan.dailyImageCap.toLocaleString()
          : `${plan.dailyImageCap.toLocaleString()} (${plan.trialImageCap.toLocaleString()} in all)`,
    },
    { label: 'Free trial', valueOf: (plan) => (plan.trialDays === null ? 'No' : `${plan.trialDays} days`) },
  ];

  /** What the table is and what its prices mean, said once, above it and as its caption. */
  protected readonly comparisonNote = computed(
    () => `Every plan, one to a row. Prices are ${this.period() === 'annual' ? 'per month, billed yearly' : 'per month'}.`,
  );

  protected priceOf(plan: Plan): number {
    return this.period() === 'annual' ? plan.annualMonthlyPrice : plan.monthlyPrice;
  }

  /**
   * The price as it is being drawn, which is the real one except during the count between the two. The
   * animation is a reading of the same number, so the figure a reader ends on is always the catalogue's.
   */
  protected shownPriceOf(plan: Plan): number {
    return this.count.shownOr(plan.key, this.priceOf(plan));
  }

  protected formatMoney(amount: number): string {
    return formatMoney(this.catalogue().currency, amount);
  }

  protected formatCredits(credits: number): string {
    return credits.toLocaleString();
  }

  /**
   * The line under the price: what a year saves, or how long a trial runs. One line, because a card that
   * explains itself twice stops being read.
   */
  protected secondLineOf(plan: Plan): string | null {
    if (plan.trialDays !== null) return `${plan.trialDays} days, no card needed to look around`;
    if (this.period() !== 'annual' || plan.annualPrice <= 0) return null;

    // What a year actually costs, said on the card: a price shown per month and charged per year has to say
    // both, and the saving alone leaves a visitor to work out what they would be billed.
    const billed = `Billed yearly at ${this.formatMoney(plan.annualPrice)}`;
    return plan.annualSaving > 0 ? `${billed} — ${this.formatMoney(plan.annualSaving)} saved` : billed;
  }

  /** What the plan includes, in sentences, every figure the catalogue's. */
  protected includedIn(plan: Plan): readonly string[] {
    return [
      `${this.formatCredits(plan.credits)} AI credits a month`,
      `About ${plan.exampleMix}`,
      this.picturesOf(plan),
    ];
  }

  /**
   * The daily cap, and on a trial the cap on the whole trial beside it (B-52). Both are the catalogue's; the
   * trial's is said in the same sentence because a reader weighing a trial needs the smaller of the two.
   */
  private picturesOf(plan: Plan): string {
    const daily = `Up to ${plan.dailyImageCap.toLocaleString()} pictures a day`;
    return plan.trialImageCap === null ? daily : `${daily}, ${plan.trialImageCap.toLocaleString()} in all`;
  }

  protected periodChanged(value: readonly string[]): void {
    const chosen = value[0];
    if (chosen !== 'monthly' && chosen !== 'annual') return;

    // From what is on screen, not from the period being left: a second toggle mid-count has to carry on from
    // the figure a reader can see, or the digits jump back before counting again.
    const from = new Map(this.plans().map((plan) => [plan.key, this.shownPriceOf(plan)]));
    this.period.set(chosen);
    this.count.run(from, new Map(this.plans().map((plan) => [plan.key, this.priceOf(plan)])));
  }

  /** The chosen plan travels as a query parameter; the link stays inside the app rather than reloading it. */
  protected planParams(plan: Plan): Record<string, string> {
    return { plan: plan.key };
  }

}
