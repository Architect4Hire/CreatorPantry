import { DOCUMENT } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import {
  CpBadgeComponent,
  CpButtonComponent,
  CpCardComponent,
  CpCardGridComponent,
  CpChoiceGroupComponent,
  CpChoiceOption,
} from '@creator-pantry/ui';

import { Plan } from '../../models/plan-catalogue.models';
import { LandingCount } from './landing-count';
import { formatMoney } from './landing-money';
import { PlanCatalogueService } from '../../services/plan-catalogue.service';
import { pictureQualityLabel } from './picture-quality';

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
  imports: [
    RouterLink,
    CpCardComponent,
    CpCardGridComponent,
    CpBadgeComponent,
    CpButtonComponent,
    CpChoiceGroupComponent,
  ],
  templateUrl: './landing-pricing.component.html',
  styleUrl: './landing-pricing.component.scss',
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
   * compared.
   *
   * **A picture-quality row since AP.4.5**, because a plan now genuinely differs by one: the qualities it may
   * ask for are what it includes, and a creator choosing between plans is choosing between those. The best
   * quality a plan reaches is the comparable fact; the full list is on the card.
   *
   * The labels are the page's; every value is the plan's own, so a figure cannot drift from the card above it
   * and no number is written into this file.
   */
  protected readonly comparisonColumns: readonly PlanComparisonColumn[] = [
    { label: 'Price', valueOf: (plan) => this.formatMoney(this.priceOf(plan)) },
    { label: 'Credits a month', valueOf: (plan) => this.formatCredits(plan.credits) },
    // The card's headline, said short: what a month makes with all of it together (owner, 2026-10-28).
    { label: 'Recipes and pictures a month', valueOf: (plan) => this.exampleSummaryOf(plan) ?? '—' },
    // What the credits buy in pictures alone, above the daily cap: pictures are what a creator's credits mostly
    // go on (AP.4.5), so the most a plan can make is still a figure that decides it.
    { label: 'Most pictures a month', valueOf: (plan) => this.picturesAtDefaultQualityOf(plan) ?? '—' },
    {
      label: 'Pictures a day',
      valueOf: (plan) =>
        plan.trialImageCap === null
          ? plan.dailyImageCap.toLocaleString()
          : `${plan.dailyImageCap.toLocaleString()} (${plan.trialImageCap.toLocaleString()} in all)`,
    },
    { label: 'Best picture quality', valueOf: (plan) => this.bestQualityOf(plan) },
    { label: 'Free trial', valueOf: (plan) => (plan.trialDays === null ? 'No' : `${plan.trialDays} days`) },
  ];

  /**
   * The qualities a plan includes, in the page's words — the card's line (AP.4.5).
   *
   * The catalogue publishes them in the enum's spelling (`High2K`) and ascending, so the last is the best — the
   * section's own `Billing:Plans` writes `high-2k`, and the server normalises it before publishing (AP.4.5), so
   * nothing here compares spellings. A class this bundle cannot word is left out rather than shown as its raw
   * key, and a plan whose classes are all unwordable reads as stating none, which the card then does not print.
   */
  protected qualitiesOf(plan: Plan): readonly string[] {
    return plan.qualityClasses.map(pictureQualityLabel).filter((label): label is string => label !== null);
  }

  /** The best quality a plan reaches, for the comparison row; a dash when it states none. */
  protected bestQualityOf(plan: Plan): string {
    return this.qualitiesOf(plan).at(-1) ?? '—';
  }

  /**
   * How many pictures the plan's credits buy at the usual quality, as the table and the card say it — or null
   * when the catalogue publishes no figure.
   *
   * "In all" rather than "a month" on a trial: its credits are granted once for the whole trial (B-48), so a
   * monthly figure would be a promise of twelve times what it gives. The server's number either way; the
   * choice of words is the only thing decided here.
   */
  protected picturesAtDefaultQualityOf(plan: Plan): string | null {
    if (plan.picturesAtDefaultQuality === null) return null;

    const count = plan.picturesAtDefaultQuality.toLocaleString();

    return plan.trialDays === null ? count : `${count} in all`;
  }

  /**
   * The comparison's short form of the card's headline: "60 recipes, 70 pictures", with "in all" on a trial for
   * the reason {@link picturesAtDefaultQualityOf} gives. Null when the plan states no counts.
   */
  protected exampleSummaryOf(plan: Plan): string | null {
    const counts = plan.exampleCounts;
    if (counts === null) return null;

    const summary = `${counts.recipeDrafts.toLocaleString()} recipes, ${counts.pictures.toLocaleString()} pictures`;
    return plan.trialDays === null ? summary : `${summary} in all`;
  }

  /**
   * The line above the card's pitch rows. "All together" because the figures are made in the same period —
   * the server holds the mix to the plan's credits as a sum — and a reader seeing the counts listed should not
   * have to wonder whether it is one or the other.
   */
  protected pitchLeadOf(plan: Plan): string {
    // The trial's length is the line above; said again here it wraps, and pushes the trial's rows below the rest.
    return plan.trialDays === null ? 'Every month, all together' : 'In the trial, all together';
  }

  /** "standard pictures" — the pictures row's words, named with the quality the count is in, as the capacity line is. */
  protected pitchPicturesLabel(): string {
    const quality = this.defaultQualityWord();
    return quality === null ? 'pictures' : `${quality} pictures`;
  }

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
      // A trial's credits are granted once (B-48), so "a month" would promise a renewal it never has.
      `${this.formatCredits(plan.credits)} AI credits ${plan.trialDays === null ? 'a month' : 'for the trial'}`,
      // The mix as a sentence only when the card has no counts to lead with; with them it is the headline.
      ...(plan.exampleCounts === null ? [`About ${plan.exampleMix}`] : []),
      // What the credits buy in pictures alone, straight after what they buy in all: the largest single thing a
      // creator spends them on (AP.4.5).
      ...this.capacityLineOf(plan),
      this.picturesOf(plan),
      // Which qualities the plan may ask for (AP.4.5), which is now a real difference between plans rather
      // than a field nothing rendered. Left off when the catalogue states none, rather than printed empty.
      ...this.qualitiesLineOf(plan),
    ];
  }

  /**
   * The card's capacity line (AP.4.5): the most pictures the credits make, named with the quality it counts so
   * the figure cannot be read as pictures of any kind — at High it would be a third of it.
   *
   * Beside a headline it is the alternative to it — "Or up to 90 standard pictures a month, if pictures are all
   * you make" — and is left off when it is no more than the headline's own pictures (a trial at its cap), where
   * it would offer nothing. Without one it stands alone and says "About", because the credits also buy writing.
   */
  protected capacityLineOf(plan: Plan): readonly string[] {
    const quality = this.defaultQualityWord();
    if (plan.picturesAtDefaultQuality === null || quality === null) return [];

    const most = plan.picturesAtDefaultQuality;
    const count = most.toLocaleString();
    const when = plan.trialDays === null ? 'a month' : 'in all';
    const counts = plan.exampleCounts;

    if (counts === null) return [`About ${count} ${quality} pictures ${when}`];
    if (most <= counts.pictures) return [];

    return [`Or up to ${count} ${quality} pictures ${when}, if pictures are all you make`];
  }

  /** The default quality in lower case for the middle of a sentence, or null when none is published. */
  private defaultQualityWord(): string | null {
    const fallback = this.catalogue().defaultPictureClass;

    return fallback === null ? null : (pictureQualityLabel(fallback)?.toLowerCase() ?? null);
  }

  /**
   * "Picture quality: Draft, Standard, High" — the qualities a plan includes, in the catalogue's ascending
   * order (AP.4.5). An empty array when it states none, so the card prints no line at all.
   */
  private qualitiesLineOf(plan: Plan): readonly string[] {
    const qualities = this.qualitiesOf(plan);

    return qualities.length === 0 ? [] : [`Picture quality: ${qualities.join(', ')}`];
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
