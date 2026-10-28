import { DOCUMENT } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { CpFieldComponent } from '@creator-pantry/ui';

import { GeneratedImageQualityClass } from '../../models/generated-image.models';
import { PictureClassRate, Plan } from '../../models/plan-catalogue.models';
import { PlanCatalogueService } from '../../services/plan-catalogue.service';
import { LandingCount } from './landing-count';
import { pictureQualityLabel } from './picture-quality';

/** The number a range input has just been moved to. */
function valueOf(event: Event): number {
  return Number((event.target as HTMLInputElement).value);
}

/**
 * One line of the menu: what the page calls the action, what it costs, and whether that figure is a typical one
 * ("about") or exactly what the action is charged.
 */
export interface CreditMenuLine {
  readonly label: string;
  readonly credits: number;
  /**
   * Writing settles at the tokens the model reported (AP.2.1), so its figure is typical and says "about". A
   * picture is charged its quality class's rate exactly (AP.4.5), so it never does.
   */
  readonly typical: boolean;
}

/**
 * What the page calls each picture quality on the public menu (AP.4.5).
 *
 * The studio's own wording is a creator's, mid-task ("Draft — quick, for trying an idea"); a visitor reading a
 * price list has no task yet, so each line names the thing being priced. Deliberately a second vocabulary: the
 * two are read in different places for different reasons, and a shared one would be wrong in both.
 */
export const PICTURE_CLASS_LABELS: Readonly<Record<GeneratedImageQualityClass, string>> = {
  Draft: 'A draft picture',
  Standard: 'A standard picture',
  High: 'A high-quality picture',
  High2K: 'A high-quality picture at 2K',
  High4K: 'A high-quality picture at 4K',
};

/** Where the estimator's sliders start and stop. Round numbers a creator can aim at, not a computed range. */
export const PICTURES_PER_MONTH = { min: 0, max: 300, step: 10, start: 60 } as const;
export const WRITING_PER_MONTH = { min: 0, max: 200, step: 5, start: 20 } as const;

/** What a month is taken to be where a daily cap has to be compared with a monthly figure. */
export const DAYS_A_MONTH = 30;

/** Who the estimate is addressed to when nothing has been asked for yet. */
export const NOTHING_CHOSEN = 'Move a slider to see which plan fits.';

/** Where a creator is sent when what they make is past the largest plan (owner answer 6). */
export const BEYOND_TOP_PLAN_CONTACT = 'robert@creatorpantry.com';

/**
 * "What a credit buys" and the plan estimator (LP.7).
 *
 * **Every figure is the catalogue's.** The labels below are the page's; the credits beside them, and the plan
 * allowances the estimate is measured against, arrive at runtime. With nothing published the section does not
 * render at all, exactly as the pricing section above it does.
 *
 * **It is an estimate and says so.** The menu states what one action of each kind *typically* costs; what a run
 * actually costs is settled against the provider's own usage, and the allowance is enforced server-side. The
 * estimator multiplies a creator's own two numbers by those typical figures — it promises nothing.
 *
 * **The trial is not an answer.** Its credits are a one-off rather than a monthly allowance, so the estimator
 * weighs the paid plans; "which plan fits what I make each month" is the question it is asked.
 */
@Component({
  selector: 'cp-landing-credits',
  standalone: true,
  imports: [CpFieldComponent],
  templateUrl: './landing-credits.component.html',
  styleUrl: './landing-credits.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LandingCreditsComponent {
  private readonly catalogueService = inject(PlanCatalogueService);
  private readonly document = inject(DOCUMENT);

  protected readonly catalogue = toSignal(this.catalogueService.catalogue(), {
    initialValue: PlanCatalogueService.EMPTY,
  });

  protected readonly heading = 'What a credit buys';
  protected readonly intro =
    'Credits are how CreatorPantry measures AI work, so every plan’s allowance means the same thing whichever model does the job. Your credits belong to you, not to a workspace, and they’re shared across every workspace you work in.';
  protected readonly pictures = PICTURES_PER_MONTH;
  protected readonly writing = WRITING_PER_MONTH;
  protected readonly nothingChosen = NOTHING_CHOSEN;
  protected readonly contact = BEYOND_TOP_PLAN_CONTACT;

  /**
   * The menu, in the order LP.2 lists it: what writing costs, then what a picture of each quality costs.
   *
   * **One line per quality since AP.4.5**, each at the exact credits that class is charged, read from the
   * charging table itself. Before quality classes shipped the product made one kind of picture and the menu
   * said so; now a creator chooses, and a single "a picture" line would price four of the five choices wrongly.
   * A class no plan includes never reaches here — the catalogue leaves it out.
   */
  protected readonly menu = computed<readonly CreditMenuLine[]>(() => {
    const rates = this.rates();
    if (rates === null) return [];

    return [
      { label: 'A recipe draft or post', credits: rates.textDraft, typical: true },
      { label: 'A long-form section', credits: rates.longFormSection, typical: true },
      ...this.catalogue().pictureClasses.map((row) => ({
        label: PICTURE_CLASS_LABELS[row.qualityClass],
        credits: row.perPicture,
        typical: false,
      })),
    ];
  });

  protected readonly picturesAMonth = signal<number>(PICTURES_PER_MONTH.start);
  protected readonly writingAMonth = signal<number>(WRITING_PER_MONTH.start);

  /** What the catalogue prices each kind of work at, or nothing when it states no rates. */
  protected readonly rates = computed(() => this.catalogue().rates);
  /** The paid plans, cheapest first, which are the ones an estimate can fit into. */
  protected readonly paidPlans = computed(() => this.catalogue().plans.filter((plan) => plan.trialDays === null));

  /**
   * Both halves need both: a menu with no rates has nothing to price, and an estimate with no plan has nothing
   * to fit into. A catalogue carrying one without the other leaves the section off the page rather than
   * showing half of it.
   *
   * Picture prices are not required: a catalogue with none states the writing lines and estimates writing
   * alone, which is a true if narrow answer. One with none is a deployment whose plans reach no quality class.
   */
  protected readonly renders = computed(() => this.paidPlans().length > 0 && this.rates() !== null);

  /**
   * The picture qualities the estimator offers, cheapest first, and which one it is pricing.
   *
   * One count and a choice of quality rather than a slider per class (AP.4.5): five sliders is more of the page
   * than this section can have, and a creator estimating a month does not plan a mix by quality — they picture
   * the work they make and how good it has to be.
   */
  protected readonly pictureClasses = computed(() => this.catalogue().pictureClasses);

  /** Which quality the estimate prices, defaulting to the cheapest offered until a visitor picks another. */
  protected readonly chosenClass = signal<GeneratedImageQualityClass | null>(null);

  /**
   * The class actually priced: the visitor's pick while it is still offered, else what a picture is made at
   * when nobody chooses, else the cheapest the catalogue lists.
   *
   * The catalogue's default rather than the cheapest, because the cheapest would under-estimate a month and
   * point at a plan too small for it — and the browser does not get to decide which quality is typical.
   */
  protected readonly pricedClass = computed<PictureClassRate | null>(() => {
    const classes = this.pictureClasses();
    const chosen = this.chosenClass() ?? this.catalogue().defaultPictureClass;

    return classes.find((row) => row.qualityClass === chosen) ?? classes[0] ?? null;
  });

  /**
   * What those two numbers come to a month: the writing at its typical rate, and the pictures at the quality
   * chosen. No arithmetic on a figure this page invented — both rates are the catalogue's.
   */
  protected readonly estimate = computed(() => {
    const rates = this.rates();
    if (rates === null) return 0;

    const perPicture = this.pricedClass()?.perPicture ?? 0;

    return this.picturesAMonth() * perPicture + this.writingAMonth() * rates.textDraft;
  });

  private readonly count = new LandingCount(this.document.defaultView);

  constructor() {
    inject(DestroyRef).onDestroy(() => this.count.stop());
  }

  /** The estimate as it is being drawn. The figure it lands on is the one the sliders add up to. */
  protected shownEstimate(): number {
    return this.count.shownOr('estimate', this.estimate());
  }

  /**
   * The smallest paid plan that covers the estimate, or null when nothing does.
   *
   * Credits are not the only thing that has to carry it: a plan also caps pictures a day, and one that allows
   * the credits but not the pictures does not fit. A month is taken as thirty days, which is of a piece with
   * the rest of an estimate.
   */
  protected readonly fits = computed<Plan | null>(() => {
    const estimate = this.estimate();
    const pictures = this.picturesAMonth();
    return this.paidPlans().find((plan) => plan.credits >= estimate && plan.dailyImageCap * DAYS_A_MONTH >= pictures) ?? null;
  });

  /**
   * What the estimate comes to, in a sentence.
   *
   * Nothing asked for is its own answer rather than "the cheapest plan fits": a visitor who has moved no slider
   * has told the page nothing, and naming a plan would be answering a question they did not ask.
   */
  protected readonly verdict = computed<string>(() => {
    const estimate = this.estimate();
    if (estimate === 0) return this.nothingChosen;

    const plan = this.fits();
    if (plan === null) {
      const top = this.paidPlans().at(-1);
      return `That is more than ${top?.name ?? 'the largest plan'} includes. Add a pack, or write to ${this.contact} and we’ll work something out.`;
    }

    const headroom = plan.credits - estimate;
    return headroom === 0
      ? `Your estimate fits ${plan.name} exactly, with none to spare.`
      : `Your estimate fits ${plan.name}, with about ${headroom.toLocaleString()} credits to spare.`;
  });

  protected formatCredits(credits: number): string {
    return credits.toLocaleString();
  }

  /**
   * What the quality choice says under itself: which quality the estimate starts from, named.
   *
   * Said rather than left to the select's own value, because the figure above it is already counting when a
   * visitor arrives, and a number with no quality attached is the misreading this whole section exists to
   * avoid — at High the same month costs nearly twice as much.
   */
  protected readonly qualityHint = computed(() => {
    const started = this.catalogue().defaultPictureClass;

    return started === null
      ? 'Each picture is charged its quality’s own rate.'
      : `Priced at ${pictureQualityLabel(started)} quality, which is what a picture is unless you choose `
          + 'another. Each quality is charged its own rate.';
  });

  /** The label the menu and the quality choice both use, so the two never word a class differently. */
  protected labelFor(qualityClass: GeneratedImageQualityClass): string {
    return PICTURE_CLASS_LABELS[qualityClass];
  }

  protected qualityChanged(event: Event): void {
    const picked = (event.target as HTMLSelectElement).value as GeneratedImageQualityClass;

    this.moveTo(() => this.chosenClass.set(picked));
  }

  protected picturesChanged(event: Event): void {
    this.moveTo(() => this.picturesAMonth.set(valueOf(event)));
  }

  protected writingChanged(event: Event): void {
    this.moveTo(() => this.writingAMonth.set(valueOf(event)));
  }

  /** Counts from the figure on screen to whatever the slider has just made it. */
  private moveTo(change: () => void): void {
    const from = this.shownEstimate();
    change();
    this.count.run(new Map([['estimate', from]]), new Map([['estimate', this.estimate()]]));
  }
}
